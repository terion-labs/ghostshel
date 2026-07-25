namespace GhostShell.Core.Tests;

public sealed class ThemePreferenceTests
{
    [Fact]
    public void Automatic_theme_follows_host_profile_scheme_and_accent()
    {
        var hostAccent = RgbColor.Parse("#2468AC");
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            hostAccent,
            supportsLiquidGlass: true);

        var effective = ThemePreference.Default.Resolve(host);

        Assert.Equal(EffectiveAppearanceMode.Dark, effective.Appearance);
        Assert.Equal(PlatformProfile.MacOsLiquidGlass, effective.PlatformProfile);
        Assert.Equal(hostAccent, effective.Accent);
        Assert.Equal(AccentSource.Host, effective.AccentSource);
    }

    [Fact]
    public void Host_accessibility_choices_override_visual_effects()
    {
        var preference = CreatePreference(AccentPreference.FollowHost);
        var host = new HostAppearance(
            HostOperatingSystem.Windows,
            HostColorScheme.Light,
            accent: null,
            highContrast: true,
            reducedMotion: true,
            reducedTransparency: true,
            textScale: 1.5,
            supportsAdvancedMaterials: true);

        var effective = preference.Resolve(host);

        Assert.True(effective.HighContrast);
        Assert.False(effective.MotionEnabled);
        Assert.False(effective.AdvancedMaterialsEnabled);
        Assert.Equal(1.5, effective.TextScale);
        Assert.Equal(ThemePreference.BronzeFallback, effective.Accent);
        Assert.Equal(AccentSource.GhostShellFallback, effective.AccentSource);
    }

    [Fact]
    public void Custom_accent_has_priority_over_the_host_accent()
    {
        var customAccent = RgbColor.Parse("#C04080");
        var preference = CreatePreference(AccentPreference.Custom(customAccent));
        var host = new HostAppearance(
            HostOperatingSystem.Linux,
            HostColorScheme.Dark,
            RgbColor.Parse("#0088CC"),
            LinuxDesktopEnvironment.Kde);

        var effective = preference.Resolve(host);

        Assert.Equal(customAccent, effective.Accent);
        Assert.Equal(AccentSource.Custom, effective.AccentSource);
        Assert.Equal(PlatformProfile.Kde, effective.PlatformProfile);
    }

    [Fact]
    public void Application_text_scale_override_replaces_the_host_scale()
    {
        var preference = CreatePreference(
            AccentPreference.FollowHost,
            textScaleOverride: 2);
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            textScale: 1.25);

        Assert.Equal(2, preference.Resolve(host).TextScale);
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(4.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Application_text_scale_override_rejects_invalid_values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePreference(
            AccentPreference.FollowHost,
            value));
    }

    [Fact]
    public void Liquid_glass_preference_falls_back_when_the_host_does_not_support_it()
    {
        var preference = new ThemePreference(
            new ThemePreferenceId("classic-fallback"),
            "Classic fallback",
            AppearanceMode.System,
            PlatformProfile.MacOsLiquidGlass,
            AccentPreference.GhostShellBronze);
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Light,
            accent: null,
            supportsLiquidGlass: false);

        Assert.Equal(PlatformProfile.MacOsClassic, preference.Resolve(host).PlatformProfile);
    }

    [Fact]
    public void Profile_identifiers_reject_whitespace()
    {
        Assert.Throws<ArgumentException>(() => new ThemePreferenceId(" "));
        Assert.Throws<ArgumentException>(() => new TerminalProfileId(" "));
        Assert.Throws<ArgumentException>(() => new KeymapProfileId(" "));
        Assert.Throws<ArgumentException>(() => new QuickTerminalSettingsId(" "));
        Assert.Throws<ArgumentException>(() => new CommandId(" "));
    }

    private static ThemePreference CreatePreference(
        AccentPreference accent,
        double? textScaleOverride = null) => new(
        new ThemePreferenceId("automatic"),
        "Automatic",
        AppearanceMode.System,
        PlatformProfile.Automatic,
        accent,
        textScaleOverride);
}
