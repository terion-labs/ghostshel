using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Browser.Tests;

public sealed class BrowserCapabilityProfileTests
{
    [Fact]
    public void ProductionAndCandidateProfilesAreBoundedAndImmutable()
    {
        Assert.Equal(
        [
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserStop,
        ],
            BrowserCapabilityProfile.Production.Capabilities.Values);
        Assert.Equal(
        [
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserStop,
        ],
            BrowserCapabilityProfile.FullAutomationCandidate.Capabilities.Values);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)BrowserCapabilityProfile
                    .Production
                    .Capabilities
                    .Values)
                .Clear());
    }

    [Fact]
    public async Task FactoryFixesOneExplicitProfileForEveryCreatedSession()
    {
        var factory = new BrowserPanelSessionFactory(
            BrowserCapabilityProfile.FullAutomationCandidate);
        await using var session = await factory.CreateAsync(
            new SessionId("browser-profile"),
            BrowserAddress.Blank,
            CancellationToken.None);

        Assert.Same(
            BrowserCapabilityProfile.FullAutomationCandidate,
            factory.CapabilityProfile);
        Assert.Same(
            BrowserCapabilityProfile.FullAutomationCandidate.Capabilities,
            factory.Capabilities);
        Assert.Same(
            BrowserCapabilityProfile.FullAutomationCandidate.Capabilities,
            session.Capabilities);
    }
}
