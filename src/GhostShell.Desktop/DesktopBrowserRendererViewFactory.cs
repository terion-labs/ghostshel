using GhostShell.App;
using GhostShell.Browser;

namespace GhostShell.Desktop;

internal sealed class DesktopBrowserRendererViewFactory(
    BrowserPanelSessionFactory sessionFactory) : IBrowserRendererViewFactory
{
    public BrowserRendererView Create()
    {
        var surface = new BrowserSurface(sessionFactory.CapabilityProfile);
        return new BrowserRendererView(surface, surface, surface);
    }
}
