using Avalonia.Automation;
using Avalonia.Controls;

namespace GhostShell.App.Tests;

public sealed class MacOsLiveRegionSafetyTests
{
    [Theory]
    [InlineData(AutomationLiveSetting.Polite)]
    [InlineData(AutomationLiveSetting.Assertive)]
    public void MacOs_suppresses_native_live_region_announcements(
        AutomationLiveSetting requested)
    {
        var liveRegion = new TextBlock();
        AutomationProperties.SetLiveSetting(liveRegion, requested);

        App.SuppressMacOsLiveRegion(liveRegion);

        Assert.Equal(
            AutomationLiveSetting.Off,
            AutomationProperties.GetLiveSetting(liveRegion));
    }
}
