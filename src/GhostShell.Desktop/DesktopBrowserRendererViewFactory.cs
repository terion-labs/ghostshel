using GhostShell.App;
using GhostShell.Application;
using GhostShell.Browser;
using GhostShell.Core;
using GhostShell.Files;

namespace GhostShell.Desktop;

internal sealed class DesktopBrowserRendererViewFactory(
    BrowserPanelSessionFactory sessionFactory,
    SshNetBrowserTunnelFactory tunnelFactory,
    CefBrowserProfileStore profileStore) : IBrowserRendererViewFactory, IDisposable
{
    private readonly object _routeGate = new();
    private readonly Dictionary<RemoteRouteKey, RemoteRoute> _remoteRoutes = [];
    private bool _disposed;

    public BrowserRendererView Create()
    {
        var profile = profileStore.AcquireLocal(BrowserProfileKey.Global);
        return CreateView(profile);
    }

    public BrowserRendererView CreateIsolatedHtmlPreview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var surface = BrowserSurface.CreateIsolatedHtmlPreview(
            sessionFactory.CapabilityProfile);
        return new BrowserRendererView(
            surface,
            surface,
            surface,
            surface.SetAgentActivity);
    }

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken) => CreateAsync(
            connection,
            BrowserProfileKey.Global,
            cancellationToken);

    public async ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileKey profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Endpoint is ConnectionEndpoint.Local)
        {
            return CreateView(profileStore.AcquireLocal(profile));
        }

        if (connection.Endpoint is not ConnectionEndpoint.Ssh)
        {
            throw new InvalidOperationException(
                $"{connection.ConnectionKind} connections cannot route a browser.");
        }

        var route = await AcquireRemoteRouteAsync(
            profile,
            connection,
            cancellationToken).ConfigureAwait(true);
        CefBrowserProfileLease? profileLease = null;
        try
        {
            profileLease = profileStore.AcquireRouted(
                profile,
                connection.Id.Value,
                route.LocalPort);
            var surface = new BrowserSurface(
                sessionFactory.CapabilityProfile,
                profileLease);
            profileLease = null;
            return new BrowserRendererView(
                surface,
                surface,
                new RoutedBrowserLifetime(
                    surface,
                    () => ReleaseRemoteRoute(route)),
                surface.SetAgentActivity);
        }
        catch
        {
            profileLease?.Dispose();
            ReleaseRemoteRoute(route);
            throw;
        }
    }

    public void Dispose()
    {
        RemoteRoute[] routes;
        lock (_routeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            routes = [.. _remoteRoutes.Values];
            _remoteRoutes.Clear();
        }

        foreach (var route in routes)
        {
            route.Tunnel.Dispose();
        }
    }

    private BrowserRendererView CreateView(CefBrowserProfileLease profile)
    {
        try
        {
            var surface = new BrowserSurface(
                sessionFactory.CapabilityProfile,
                profile);
            return new BrowserRendererView(
                surface,
                surface,
                surface,
                surface.SetAgentActivity);
        }
        catch
        {
            profile.Dispose();
            throw;
        }
    }

    private async ValueTask<RemoteRoute> AcquireRemoteRouteAsync(
        BrowserProfileKey profile,
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        var key = new RemoteRouteKey(profile, connection.Id);
        lock (_routeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remoteRoutes.TryGetValue(key, out var existing))
            {
                existing.ActiveBrowsers++;
                return existing;
            }
        }

        var tunnel = await tunnelFactory
            .OpenAsync(connection, cancellationToken)
            .ConfigureAwait(true);
        lock (_routeGate)
        {
            if (_disposed)
            {
                tunnel.Dispose();
                throw new ObjectDisposedException(nameof(DesktopBrowserRendererViewFactory));
            }

            if (_remoteRoutes.TryGetValue(key, out var raced))
            {
                raced.ActiveBrowsers++;
                tunnel.Dispose();
                return raced;
            }

            var created = new RemoteRoute(key, tunnel)
            {
                ActiveBrowsers = 1,
            };
            _remoteRoutes.Add(key, created);
            return created;
        }
    }

    private void ReleaseRemoteRoute(RemoteRoute route)
    {
        var dispose = false;
        lock (_routeGate)
        {
            if (route.ActiveBrowsers <= 0)
            {
                return;
            }

            route.ActiveBrowsers--;
            if (route.ActiveBrowsers == 0)
            {
                _remoteRoutes.Remove(route.Key);
                dispose = true;
            }
        }

        if (dispose)
        {
            route.Tunnel.Dispose();
        }
    }

    private sealed class RoutedBrowserLifetime(
        BrowserSurface surface,
        Action releaseRoute) : IDisposable
    {
        private Action? _releaseRoute = releaseRoute;

        public void Dispose()
        {
            try
            {
                surface.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref _releaseRoute, null)?.Invoke();
            }
        }
    }

    private readonly record struct RemoteRouteKey(
        BrowserProfileKey Profile,
        ConnectionId ConnectionId);

    private sealed class RemoteRoute(
        RemoteRouteKey key,
        SshNetBrowserTunnelFactory.SshBrowserTunnel tunnel)
    {
        public RemoteRouteKey Key { get; } = key;

        public SshNetBrowserTunnelFactory.SshBrowserTunnel Tunnel { get; } = tunnel;

        public int LocalPort => Tunnel.LocalPort;

        public int ActiveBrowsers { get; set; }
    }
}
