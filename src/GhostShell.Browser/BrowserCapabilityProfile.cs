using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Names the complete browser contract that a factory, session, and renderer
/// agree to expose for their full lifetime.
/// </summary>
public sealed class BrowserCapabilityProfile
{
    /// <summary>
    /// The capability set used by desktop production until a named native
    /// adapter has passed the automation conformance suite.
    /// </summary>
    public static BrowserCapabilityProfile Production { get; } = new(
    [
        SessionCapabilities.BrowserReadState,
        SessionCapabilities.BrowserNavigate,
        SessionCapabilities.BrowserBack,
        SessionCapabilities.BrowserForward,
        SessionCapabilities.BrowserReload,
        SessionCapabilities.BrowserStop,
        SessionCapabilities.BrowserOriginGuard,
    ]);

    /// <summary>
    /// The complete implemented browser contract. Tests inject this profile
    /// explicitly; production may select it only with platform conformance
    /// evidence.
    /// </summary>
    public static BrowserCapabilityProfile FullAutomationCandidate { get; } = new(
    [
        .. Production.Capabilities.Values,
        SessionCapabilities.BrowserSnapshot,
        SessionCapabilities.BrowserClick,
        SessionCapabilities.BrowserFill,
        SessionCapabilities.BrowserCheck,
    ]);

    private BrowserCapabilityProfile(IEnumerable<string> capabilities)
    {
        Capabilities = new CapabilitySet(capabilities);
    }

    public CapabilitySet Capabilities { get; }

    internal bool Supports(string capability) =>
        Capabilities.Contains(capability);
}
