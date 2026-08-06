using GhostShell.Core;

namespace GhostShell.App;

internal static class QuickTerminalPresentationPolicy
{
    public static bool ShouldAnimate(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        return settings.AnimateSlide
            && !settings.ReduceMotion
            && !hostPreferences.ReducedMotion
            && settings.AnimationDurationMilliseconds > 0;
    }

    public static bool ShouldUseBlur(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        return settings.IsTranslucent && !hostPreferences.ReducedTransparency;
    }

    public static double EffectiveOpacity(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        return hostPreferences.ReducedTransparency ? 1 : settings.Opacity;
    }
}
