using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class QuickTerminalPresentationPolicyTests
{
    [Fact]
    public void Default_presentation_uses_the_stored_motion_blur_and_opacity()
    {
        var settings = QuickTerminalSettings.Default;

        Assert.True(QuickTerminalPresentationPolicy.ShouldAnimate(
            settings,
            HostAccessibilityPreferences.Default));
        Assert.True(QuickTerminalPresentationPolicy.ShouldUseBlur(
            settings,
            HostAccessibilityPreferences.Default));
        Assert.Equal(
            settings.Opacity,
            QuickTerminalPresentationPolicy.EffectiveOpacity(
                settings,
                HostAccessibilityPreferences.Default));
    }

    [Fact]
    public void Host_accessibility_preferences_override_visual_effects()
    {
        var host = new HostAccessibilityPreferences(
            reducedMotion: true,
            reducedTransparency: true,
            textScale: 1);

        Assert.False(QuickTerminalPresentationPolicy.ShouldAnimate(
            QuickTerminalSettings.Default,
            host));
        Assert.False(QuickTerminalPresentationPolicy.ShouldUseBlur(
            QuickTerminalSettings.Default,
            host));
        Assert.Equal(
            1,
            QuickTerminalPresentationPolicy.EffectiveOpacity(
                QuickTerminalSettings.Default,
                host));
    }

    [Fact]
    public void Stored_reduce_motion_remains_more_restrictive_than_the_host()
    {
        var defaults = QuickTerminalSettings.Default;
        var settings = new QuickTerminalSettings(
            defaults.Id,
            defaults.Name,
            defaults.Hotkey,
            defaults.MonitorPolicy,
            defaults.HeightFraction,
            defaults.Opacity,
            defaults.AnimateSlide,
            defaults.AnimationDurationMilliseconds,
            reduceMotion: true,
            restoreLastSession: defaults.RestoreLastSession,
            hideOnFocusLoss: defaults.HideOnFocusLoss,
            isTranslucent: defaults.IsTranslucent);

        Assert.False(QuickTerminalPresentationPolicy.ShouldAnimate(
            settings,
            HostAccessibilityPreferences.Default));
    }
}
