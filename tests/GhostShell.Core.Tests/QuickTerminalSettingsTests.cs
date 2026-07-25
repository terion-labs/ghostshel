using System.Text.Json;

namespace GhostShell.Core.Tests;

public sealed class QuickTerminalSettingsTests
{
    [Fact]
    public void Defaults_match_the_functional_drop_down_profile()
    {
        var settings = QuickTerminalSettings.Default;

        Assert.Equal(DefinitionKind.QuickTerminalSettings, settings.Key.Kind);
        Assert.Equal(new KeyStroke("GRAVE", KeyModifiers.Meta), settings.Hotkey);
        Assert.Equal(QuickTerminalMonitorPolicy.MainWindow, settings.MonitorPolicy);
        Assert.Equal(0.55, settings.HeightFraction);
        Assert.Equal(0.82, settings.Opacity);
        Assert.Equal(24, settings.BlurRadius);
        Assert.True(settings.AnimateSlide);
        Assert.True(settings.RestoreLastSession);
        Assert.True(settings.HideOnFocusLoss);
    }

    [Theory]
    [InlineData(0.24, 0.82, 24, 180)]
    [InlineData(0.91, 0.82, 24, 180)]
    [InlineData(0.55, 0.29, 24, 180)]
    [InlineData(0.55, 1.01, 24, 180)]
    [InlineData(0.55, 0.82, -1, 180)]
    [InlineData(0.55, 0.82, 101, 180)]
    [InlineData(0.55, 0.82, 24, -1)]
    [InlineData(0.55, 0.82, 24, 1001)]
    public void Invalid_visual_ranges_are_rejected(
        double height,
        double opacity,
        int blur,
        int duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(
            height,
            opacity,
            blur,
            duration));
    }

    [Fact]
    public void Durable_payload_round_trips_every_behavior_choice()
    {
        var original = new QuickTerminalSettings(
            new QuickTerminalSettingsId("custom"),
            "Custom Quick Terminal",
            new KeyStroke("K", KeyModifiers.Control | KeyModifiers.Shift),
            QuickTerminalMonitorPolicy.Primary,
            0.4,
            0.7,
            0,
            animateSlide: false,
            animationDurationMilliseconds: 0,
            reduceMotion: true,
            restoreLastSession: false,
            hideOnFocusLoss: false);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<QuickTerminalSettings>(json);

        Assert.Equal(original, restored);
    }

    private static QuickTerminalSettings Create(
        double height,
        double opacity,
        int blur,
        int duration) => new(
            new QuickTerminalSettingsId("test"),
            "Test",
            new KeyStroke("GRAVE", KeyModifiers.Meta),
            QuickTerminalMonitorPolicy.MainWindow,
            height,
            opacity,
            blur,
            animateSlide: true,
            animationDurationMilliseconds: duration,
            reduceMotion: false,
            restoreLastSession: true,
            hideOnFocusLoss: true);
}
