using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class IsolatedBrowserRendererViewFactory :
    IBrowserRendererViewFactory,
    IAsyncDisposable
{
    private readonly Dictionary<ConnectionId, WorkspaceIsolationSocksProxy> _routeProxies = [];
    private readonly object _gate = new();
    private readonly IBrowserRendererViewFactory _inner;
    private readonly IConnectionCommandRuntime _commandRuntime;
    private readonly WorkspaceIsolationSocksProxy _workspaceProxy;
    private readonly string _routeIdentity;

    public IsolatedBrowserRendererViewFactory(
        IBrowserRendererViewFactory inner,
        IConnectionCommandRuntime commandRuntime,
        WorkspaceIsolationSocksProxy workspaceProxy,
        string routeIdentity)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _commandRuntime = commandRuntime ?? throw new ArgumentNullException(nameof(commandRuntime));
        _workspaceProxy = workspaceProxy ?? throw new ArgumentNullException(nameof(workspaceProxy));
        _routeIdentity = routeIdentity;
    }

    public BrowserRendererView Create() => _inner.Create();

    public BrowserRendererView CreateIsolatedHtmlPreview() =>
        _inner.CreateIsolatedHtmlPreview();

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        CreateAsync(
            connection,
            BrowserProfileBinding.Legacy(BrowserProfileKey.Global),
            cancellationToken);

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileKey profile,
        CancellationToken cancellationToken) =>
        CreateAsync(
            connection,
            BrowserProfileBinding.Legacy(profile),
            cancellationToken);

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileBinding profile,
        CancellationToken cancellationToken)
    {
        var proxy = ProxyFor(connection);
        return _inner.CreateThroughSocksProxyAsync(
            proxy.LocalPort,
            $"{_routeIdentity}:{connection.Id.Value}",
            profile,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        WorkspaceIsolationSocksProxy[] proxies;
        lock (_gate)
        {
            proxies = [.. _routeProxies.Values];
            _routeProxies.Clear();
        }

        foreach (var proxy in proxies)
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    private WorkspaceIsolationSocksProxy ProxyFor(ConnectionProfile connection)
    {
        if (connection.Endpoint is ConnectionEndpoint.Local)
        {
            return _workspaceProxy;
        }

        lock (_gate)
        {
            if (_routeProxies.TryGetValue(connection.Id, out var existing))
            {
                return existing;
            }

            var created = new WorkspaceIsolationSocksProxy(_commandRuntime, connection);
            _routeProxies.Add(connection.Id, created);
            return created;
        }
    }
}
