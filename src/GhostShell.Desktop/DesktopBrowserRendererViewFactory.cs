using GhostShell.App;
using GhostShell.Browser;
using GhostShell.Core;
using GhostShell.Files;

namespace GhostShell.Desktop;

internal sealed class DesktopBrowserRendererViewFactory(
    BrowserPanelSessionFactory sessionFactory,
    SshNetBrowserTunnelFactory tunnelFactory) : IBrowserRendererViewFactory
{
    public BrowserRendererView Create()
    {
        var surface = new BrowserSurface(sessionFactory.CapabilityProfile);
        return new BrowserRendererView(
            surface,
            surface,
            surface,
            surface.SetAgentActivity);
    }

    public async ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Endpoint is ConnectionEndpoint.Local)
        {
            return Create();
        }

        if (connection.Endpoint is not ConnectionEndpoint.Ssh)
        {
            throw new InvalidOperationException(
                $"{connection.ConnectionKind} connections cannot route a browser.");
        }

        var tunnel = await tunnelFactory
            .OpenAsync(connection, cancellationToken)
            .ConfigureAwait(true);
        try
        {
            var surface = new BrowserSurface(
                sessionFactory.CapabilityProfile,
                tunnel.LocalPort);
            return new BrowserRendererView(
                surface,
                surface,
                new RoutedBrowserLifetime(surface, tunnel),
                surface.SetAgentActivity);
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
    }

    private sealed class RoutedBrowserLifetime(
        BrowserSurface surface,
        IDisposable tunnel) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                surface.Dispose();
            }
            finally
            {
                tunnel.Dispose();
            }
        }
    }
}
