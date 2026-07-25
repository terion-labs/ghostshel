namespace GhostShell.Application;

public interface IBrowserPanelSession :
    IPanelSession,
    IBrowserNavigation,
    IOriginConstrainedBrowserNavigation,
    IOriginConstrainedBrowserElementClick,
    IOriginConstrainedBrowserElementFill,
    IOriginConstrainedBrowserElementCheck,
    IBrowserDocumentReader,
    IBrowserRendererAttachment
{
}
