using GhostShell.App.Views;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class MacOsApplicationIconTests
{
    [Fact]
    public void Native_application_accepts_the_generated_icon()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(26))
        {
            return;
        }

        Assert.True(MacOsApplicationIcon.TryApply(new RgbColor(247, 130, 27)));
    }

    [Fact]
    public void Light_icon_uses_system_accent_behind_a_black_mark()
    {
        var svg = MacOsApplicationIcon.CreateSvg(new RgbColor(247, 130, 27), dark: false);

        Assert.Contains("fill=\"#F7821B\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#000000\"", svg, StringComparison.Ordinal);
        Assert.True(
            svg.IndexOf("fill=\"#F7821B\"", StringComparison.Ordinal)
            < svg.IndexOf("fill=\"#000000\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Dark_icon_inverts_system_accent_and_black()
    {
        var svg = MacOsApplicationIcon.CreateSvg(new RgbColor(247, 130, 27), dark: true);

        Assert.Contains("fill=\"#000000\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#F7821B\"", svg, StringComparison.Ordinal);
        Assert.True(
            svg.IndexOf("fill=\"#000000\"", StringComparison.Ordinal)
            < svg.IndexOf("fill=\"#F7821B\"", StringComparison.Ordinal));
    }
}
