using GhostShell.App.Views;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class MainWindowAppearanceTests
{
    [Fact]
    public void Platform_profile_picker_includes_every_durable_profile()
    {
        Assert.Equal(
            Enum.GetValues<PlatformProfile>(),
            MainWindow.AppearancePlatformProfiles);
        Assert.Contains(
            PlatformProfile.Custom,
            MainWindow.AppearancePlatformProfiles);
    }
}
