using Exclr8Cef;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Owns the ephemeral CEF request contexts behind GhostSHELL browser profiles.
/// A profile revision is further sharded by network route because Chromium's proxy
/// preference belongs to the request context that also owns in-memory state.
/// No request context has a cache path. The final lease always destroys it.
/// </summary>
public sealed class CefBrowserProfileStore : IBrowserProfileDataControl, IDisposable
{
    private const string LocalRoute = "local";
    private readonly object _gate = new();
    private readonly SemaphoreSlim _clearGate = new(1, 1);
    private readonly IBrowserProfileAuthenticationResolver? _authenticationResolver;
    private readonly Func<ICefBrowserRequestContext> _createContext;
    private readonly Dictionary<ContextKey, ContextEntry> _contexts = [];
    private bool _disposed;

    public CefBrowserProfileStore(
        IBrowserProfileAuthenticationResolver? authenticationResolver = null)
        : this(authenticationResolver, CefBrowserRequestContext.Create)
    {
    }

    internal CefBrowserProfileStore(
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        Func<ICefBrowserRequestContext> createContext)
    {
        _authenticationResolver = authenticationResolver;
        _createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
    }

    public CefBrowserProfileLease AcquireLocal(BrowserProfileKey profile) =>
        AcquireLocal(BrowserProfileBinding.Legacy(profile));

    public CefBrowserProfileLease AcquireLocal(BrowserProfileBinding profile) =>
        Acquire(profile, LocalRoute, socksProxyPort: null);

    public CefBrowserProfileLease AcquireRouted(
        BrowserProfileKey profile,
        string routeIdentity,
        int socksProxyPort)
        => AcquireRouted(
            BrowserProfileBinding.Legacy(profile),
            routeIdentity,
            socksProxyPort);

    public CefBrowserProfileLease AcquireRouted(
        BrowserProfileBinding profile,
        string routeIdentity,
        int socksProxyPort)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        return Acquire(profile, routeIdentity, socksProxyPort);
    }

    public BrowserProfileDataState ReadState(
        BrowserProfileSelection selection,
        long expectedRevision)
    {
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var matching = _contexts
                .Where(item => item.Key.Selection == selection
                    && item.Key.Revision == expectedRevision)
                .Select(item => item.Value)
                .ToArray();
            return new BrowserProfileDataState(
                selection,
                expectedRevision,
                matching.Length,
                matching.Sum(item => item.ActiveLeases));
        }
    }

    public async ValueTask<BrowserProfileClearResult> ClearAsync(
        BrowserProfileClearRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await _clearGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            return Clear(request, cancellationToken);
        }
        finally
        {
            _clearGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _contexts.Values)
            {
                entry.Context.Dispose();
            }

            _contexts.Clear();
        }

    }

    internal void Release(ContextKey key)
    {
        lock (_gate)
        {
            if (_disposed || !_contexts.TryGetValue(key, out var entry))
            {
                return;
            }

            if (entry.ActiveLeases <= 0)
            {
                throw new InvalidOperationException(
                    "The browser profile lease count is already zero.");
            }

            entry.ActiveLeases--;
            if (entry.ActiveLeases == 0)
            {
                entry.Context.Dispose();
                _contexts.Remove(key);
            }
        }
    }

    private CefBrowserProfileLease Acquire(
        BrowserProfileBinding profile,
        string routeIdentity,
        int? socksProxyPort)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var key = new ContextKey(
            profile.Selection,
            profile.Revision,
            RouteKey(routeIdentity));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_contexts.TryGetValue(key, out var existing))
            {
                if (existing.SocksProxyPort != socksProxyPort)
                {
                    throw new InvalidOperationException(
                        "The browser profile route is already active through a different proxy endpoint.");
                }

                existing.ActiveLeases++;
                return new CefBrowserProfileLease(
                    this,
                    key,
                    existing.Context,
                    profile,
                    _authenticationResolver,
                    existing.SocksProxyPort is null
                        ? BrowserNetworkRouteKind.Local
                        : BrowserNetworkRouteKind.SshRouted);
            }

            var context = _createContext();
            try
            {
                if (socksProxyPort is { } port)
                {
                    foreach (var preference in
                             CefBrowserNetworkContext.RequiredPreferences(port))
                    {
                        if (!context.SetPreference(preference.Key, preference.Value))
                        {
                            throw new InvalidOperationException(
                                $"The embedded browser rejected the required '{preference.Key}' network setting.");
                        }
                    }
                }

                _contexts.Add(
                    key,
                    new ContextEntry(context, socksProxyPort)
                    {
                        ActiveLeases = 1,
                    });
                return new CefBrowserProfileLease(
                    this,
                    key,
                    context,
                    profile,
                    _authenticationResolver,
                    socksProxyPort is null
                        ? BrowserNetworkRouteKind.Local
                        : BrowserNetworkRouteKind.SshRouted);
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }
    }

    private BrowserProfileClearResult Clear(
        BrowserProfileClearRequest request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            var otherRevision = _contexts.Keys.Any(key =>
                key.Selection == request.Selection
                && key.Revision != request.ExpectedRevision);
            if (otherRevision)
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.RevisionMismatch,
                    0,
                    "An open browser still owns another revision of this profile. Close it before clearing data.");
            }

            var matching = _contexts
                .Where(item => item.Key.Selection == request.Selection
                    && item.Key.Revision == request.ExpectedRevision)
                .ToArray();
            if (request.Categories.HasFlag(
                    BrowserProfileDataCategory.AllEphemeralWebContent)
                && matching.Any(item => item.Value.ActiveLeases > 0))
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.InUse,
                    0,
                    "Close browser tabs using this exact profile, then reset its ephemeral web content.");
            }

            try
            {
                foreach (var item in matching)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Cancelled();
                    }

                    if (request.Categories.HasFlag(BrowserProfileDataCategory.Cookies))
                    {
                        item.Value.Context.DeleteCookies();
                    }

                    if (request.Categories.HasFlag(
                            BrowserProfileDataCategory.HttpAuthentication))
                    {
                        item.Value.Context.ClearHttpAuthCredentials();
                        item.Value.Context.CloseAllConnections();
                    }
                }

                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.Cleared,
                    0,
                    matching.Length == 0
                        ? "This exact profile revision has no ephemeral web data."
                        : ClearMessage(request.Categories));
            }
            catch (InvalidOperationException)
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.Failed,
                    0,
                    "The embedded browser could not clear this exact profile revision.");
            }
        }
    }

    private static string RouteKey(string routeIdentity) => routeIdentity;

    private static BrowserProfileClearResult Cancelled() => new(
        BrowserProfileClearStatus.Cancelled,
        0,
        "Browser profile clearing was cancelled.");

    private static string ClearMessage(BrowserProfileDataCategory categories) =>
        categories switch
        {
            BrowserProfileDataCategory.Cookies =>
                "Cookies were cleared from this exact in-memory profile revision.",
            BrowserProfileDataCategory.HttpAuthentication =>
                "HTTP authentication was cleared from this exact in-memory profile revision.",
            _ =>
                "The selected in-memory browser data categories were cleared from this exact profile revision.",
        };

    internal static void DeleteOwnedDirectory(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (root.LinkTarget is not null
            || (root.Exists
                && root.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new IOException(
                "Browser profile storage root is an unexpected filesystem link.");
        }

        if (!root.Exists)
        {
            return;
        }

        // Startup uses this only to remove profile trees written by older
        // releases. Current request contexts never receive a cache path.
        root.Delete(recursive: true);
    }

    internal readonly record struct ContextKey(
        BrowserProfileSelection Selection,
        long Revision,
        string Route);

    private sealed class ContextEntry(
        ICefBrowserRequestContext context,
        int? socksProxyPort)
    {
        public ICefBrowserRequestContext Context { get; } = context;

        public int? SocksProxyPort { get; } = socksProxyPort;

        public int ActiveLeases { get; set; }
    }
}

/// <summary>
/// Keeps one profile context alive for one browser surface, including native
/// renderer replacement after a crash.
/// </summary>
public sealed class CefBrowserProfileLease : IDisposable
{
    private CefBrowserProfileStore? _owner;
    private readonly CefBrowserProfileStore.ContextKey _key;
    private readonly ICefBrowserRequestContext _context;
    private readonly BrowserProfileBinding _profile;
    private readonly IBrowserProfileAuthenticationResolver? _authenticationResolver;

    internal CefBrowserProfileLease(
        CefBrowserProfileStore owner,
        CefBrowserProfileStore.ContextKey key,
        ICefBrowserRequestContext context,
        BrowserProfileBinding profile,
        IBrowserProfileAuthenticationResolver? authenticationResolver,
        BrowserNetworkRouteKind routeKind)
    {
        _owner = owner;
        _key = key;
        _context = context;
        _profile = profile;
        _authenticationResolver = authenticationResolver;
        RouteKind = routeKind;
    }

    internal BrowserNetworkRouteKind RouteKind { get; }

    internal CefBrowserView CreateView() => _context.CreateView(
        _profile,
        _authenticationResolver);

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_key);
}

/// <summary>
/// Adapts the vendor request context at the single ownership boundary used by
/// the profile store. Tests replace this boundary without initializing CEF.
/// </summary>
internal interface ICefBrowserRequestContext : IDisposable
{
    bool SetPreference(string name, string value);

    void DeleteCookies();

    void ClearHttpAuthCredentials();

    void CloseAllConnections();

    CefBrowserView CreateView(
        BrowserProfileBinding profile,
        IBrowserProfileAuthenticationResolver? authenticationResolver);
}

internal sealed class CefBrowserRequestContext(
    CefRequestContext context) : ICefBrowserRequestContext
{
    private readonly CefRequestContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    public static ICefBrowserRequestContext Create() => new CefBrowserRequestContext(
        Cef.CreateRequestContext()
        ?? throw new InvalidOperationException(
            "The embedded browser could not create its ephemeral profile."));

    public bool SetPreference(string name, string value) =>
        _context.SetPreference(name, value);

    public void DeleteCookies() => _context.DeleteCookies();

    public void ClearHttpAuthCredentials() =>
        _context.ClearHttpAuthCredentials();

    public void CloseAllConnections() => _context.CloseAllConnections();

    public CefBrowserView CreateView(
        BrowserProfileBinding profile,
        IBrowserProfileAuthenticationResolver? authenticationResolver) => new(
        _context,
        CefBrowserContentPolicy.Ordinary,
        profile,
        authenticationResolver);

    public void Dispose() => _context.Dispose();
}
