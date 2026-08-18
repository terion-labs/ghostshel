using System.Security.Cryptography;
using System.Text;
using Exclr8Cef;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Owns the persistent CEF request contexts behind GhostSHELL browser
/// profiles. A profile is further sharded by network route because Chromium's
/// proxy preference belongs to the request context that also owns storage.
/// </summary>
public sealed class CefBrowserProfileStore : IBrowserProfileDataControl, IDisposable
{
    private const string LocalRoute = "local";
    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly Dictionary<ContextKey, ContextEntry> _contexts = [];
    private bool _disposed;

    public CefBrowserProfileStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_rootDirectory);
        RestrictDirectory(_rootDirectory);
    }

    public CefBrowserProfileLease AcquireLocal(BrowserProfileKey profile) =>
        Acquire(profile, LocalRoute, socksProxyPort: null);

    public CefBrowserProfileLease AcquireRouted(
        BrowserProfileKey profile,
        string routeIdentity,
        int socksProxyPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeIdentity);
        return Acquire(profile, routeIdentity, socksProxyPort);
    }

    public BrowserProfileStorageUsage ReadUsage()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        // Cache trees can contain thousands of small files. Do not hold the
        // context-lifecycle gate while callers account for them; a concurrent
        // clear may make this approximate, which is preferable to delaying a
        // browser launch for a display-only byte count.
        return new BrowserProfileStorageUsage(
            DirectoryBytes(ScopeDirectory(BrowserProfileKind.Global)),
            DirectoryBytes(ScopeDirectory(BrowserProfileKind.Workspace)),
            DirectoryBytes(ScopeDirectory(BrowserProfileKind.WebApp)));
    }

    public ValueTask<BrowserProfileClearResult> ClearAsync(
        BrowserProfileDataScope scope,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<BrowserProfileClearResult>(Task.Run(
            () => Clear(scope, cancellationToken),
            cancellationToken));
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
            if (entry.ActiveLeases == 0 && entry.SocksProxyPort is not null)
            {
                entry.Context.Dispose();
                _contexts.Remove(key);
            }
        }
    }

    private CefBrowserProfileLease Acquire(
        BrowserProfileKey profile,
        string routeIdentity,
        int? socksProxyPort)
    {
        var key = new ContextKey(profile, RouteKey(routeIdentity));
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
                return new CefBrowserProfileLease(this, key, existing.Context);
            }

            var directory = ContextDirectory(key);
            Directory.CreateDirectory(directory);
            RestrictDirectory(directory);
            var context = Cef.CreateRequestContext(directory)
                ?? throw new InvalidOperationException(
                    "The embedded browser could not create its storage profile.");
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
                return new CefBrowserProfileLease(this, key, context);
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }
    }

    private BrowserProfileClearResult Clear(
        BrowserProfileDataScope scope,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var matching = _contexts
                .Where(item => Includes(scope, item.Key.Profile.Kind))
                .ToArray();
            if (matching.Any(item => item.Value.ActiveLeases > 0))
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.InUse,
                    0,
                    "Close browser tabs using this profile, then clear it again.");
            }

            var before = ScopeBytes(scope);
            try
            {
                foreach (var item in matching)
                {
                    item.Value.Context.CloseAllConnections();
                    item.Value.Context.DeleteCookies();
                    item.Value.Context.Dispose();
                    _contexts.Remove(item.Key);
                }

                foreach (var directory in ScopeDirectories(scope))
                {
                    DeleteOwnedDirectory(directory);
                }

                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.Cleared,
                    before,
                    before == 0
                        ? "This browser profile was already empty."
                        : "Browser cookies, cache, and site storage were cleared.");
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return new BrowserProfileClearResult(
                    BrowserProfileClearStatus.Failed,
                    0,
                    "Browser profile data could not be cleared from disk.");
            }
        }
    }

    private long ScopeBytes(BrowserProfileDataScope scope) =>
        ScopeDirectories(scope).Sum(DirectoryBytes);

    private IEnumerable<string> ScopeDirectories(BrowserProfileDataScope scope)
    {
        if (scope is BrowserProfileDataScope.Global or BrowserProfileDataScope.All)
        {
            yield return ScopeDirectory(BrowserProfileKind.Global);
        }

        if (scope is BrowserProfileDataScope.Workspaces or BrowserProfileDataScope.All)
        {
            yield return ScopeDirectory(BrowserProfileKind.Workspace);
        }

        if (scope is BrowserProfileDataScope.WebApps or BrowserProfileDataScope.All)
        {
            yield return ScopeDirectory(BrowserProfileKind.WebApp);
        }
    }

    private string ContextDirectory(ContextKey key)
    {
        var profileDirectory = key.Profile.Kind == BrowserProfileKind.Global
            ? ScopeDirectory(BrowserProfileKind.Global)
            : Path.Combine(
                ScopeDirectory(key.Profile.Kind),
                StableName(key.Profile.Identity));
        return Path.Combine(profileDirectory, key.Route);
    }

    private string ScopeDirectory(BrowserProfileKind kind) => Path.Combine(
        _rootDirectory,
        kind switch
        {
            BrowserProfileKind.Global => "global",
            BrowserProfileKind.Workspace => "workspaces",
            BrowserProfileKind.WebApp => "webapps",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        });

    private static string RouteKey(string routeIdentity) =>
        string.Equals(routeIdentity, LocalRoute, StringComparison.Ordinal)
            ? LocalRoute
            : StableName(routeIdentity);

    private static string StableName(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    private static bool Includes(
        BrowserProfileDataScope scope,
        BrowserProfileKind kind) => scope == BrowserProfileDataScope.All
        || (scope == BrowserProfileDataScope.Global
            && kind == BrowserProfileKind.Global)
        || (scope == BrowserProfileDataScope.Workspaces
            && kind == BrowserProfileKind.Workspace)
        || (scope == BrowserProfileDataScope.WebApps
            && kind == BrowserProfileKind.WebApp);

    private static long DirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            long bytes = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(directory));
            while (pending.TryPop(out var current))
            {
                foreach (var item in current.EnumerateFileSystemInfos())
                {
                    if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (item is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                    else if (item is FileInfo file)
                    {
                        bytes = checked(bytes + file.Length);
                    }
                }
            }

            return bytes;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or OverflowException)
        {
            return 0;
        }
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var root = new DirectoryInfo(directory);
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || ContainsFileSystemLink(root))
        {
            throw new IOException(
                "Browser profile storage contains an unexpected filesystem link.");
        }

        Directory.Delete(directory, recursive: true);
    }

    private static bool ContainsFileSystemLink(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            foreach (var item in current.EnumerateFileSystemInfos())
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }

                if (item is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    private static void RestrictDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    internal readonly record struct ContextKey(
        BrowserProfileKey Profile,
        string Route);

    private sealed class ContextEntry(
        CefRequestContext context,
        int? socksProxyPort)
    {
        public CefRequestContext Context { get; } = context;

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
    private readonly CefRequestContext _context;

    internal CefBrowserProfileLease(
        CefBrowserProfileStore owner,
        CefBrowserProfileStore.ContextKey key,
        CefRequestContext context)
    {
        _owner = owner;
        _key = key;
        _context = context;
    }

    internal CefBrowserView CreateView() => new(_context);

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_key);
}
