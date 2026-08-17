namespace GhostShell.Application;

/// <summary>
/// A platform browser surface. Vendor controls and handles remain behind this
/// GhostSHELL-owned contract.
/// </summary>
public interface IBrowserRenderer :
    IBrowserNavigation,
    IOriginConstrainedBrowserNavigation,
    IOriginConstrainedBrowserElementClick,
    IOriginConstrainedBrowserElementFill,
    IOriginConstrainedBrowserElementCheck,
    IOriginConstrainedBrowserAutomation,
    IBrowserDocumentReader,
    IBrowserWaitObservation
{
    CapabilitySet Capabilities { get; }

    /// <remarks>
    /// Renderers may raise this event on their platform UI thread. Subscribers
    /// must return promptly and marshal longer-running work themselves.
    /// </remarks>
    event EventHandler<BrowserStateChangedEventArgs>? StateChanged;
}
