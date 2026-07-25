using Avalonia.Media;
using Avalonia.Platform;
using GhostShell.App.Views;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class AvaloniaHostAppearanceAdapterTests
{
    [Theory]
    [InlineData("ubuntu:GNOME", LinuxDesktopEnvironment.Gnome)]
    [InlineData("KDE;plasma", LinuxDesktopEnvironment.Kde)]
    [InlineData("X-Cinnamon", LinuxDesktopEnvironment.Unknown)]
    public void Linux_desktop_detection_is_case_insensitive_and_conservative(
        string desktop,
        LinuxDesktopEnvironment expected)
    {
        Assert.Equal(
            expected,
            HostPlatformCapabilities.DetectLinuxDesktop(desktop));
    }

    [Fact]
    public void Avalonia_values_map_to_the_core_host_contract()
    {
        var host = AvaloniaHostAppearanceAdapter.Map(
            PlatformThemeVariant.Light,
            (ColorContrastPreference)1,
            Color.FromRgb(0x24, 0x68, 0xAC),
            new HostPlatformCapabilities(
                HostOperatingSystem.Linux,
                LinuxDesktopEnvironment.Gnome,
                SupportsAdvancedMaterials: false,
                SupportsLiquidGlass: false));

        Assert.Equal(HostOperatingSystem.Linux, host.OperatingSystem);
        Assert.Equal(LinuxDesktopEnvironment.Gnome, host.LinuxDesktop);
        Assert.Equal(HostColorScheme.Light, host.ColorScheme);
        Assert.Equal(RgbColor.Parse("#2468AC"), host.Accent);
        Assert.True(host.HighContrast);
        Assert.False(host.SupportsAdvancedMaterials);
    }

    [Fact]
    public void Transparent_platform_accent_allows_core_bronze_fallback()
    {
        var host = AvaloniaHostAppearanceAdapter.Map(
            PlatformThemeVariant.Dark,
            ColorContrastPreference.NoPreference,
            Colors.Transparent,
            new HostPlatformCapabilities(
                HostOperatingSystem.Windows,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: false));

        var effective = ThemePreference.Default.Resolve(host);

        Assert.Null(host.Accent);
        Assert.Equal(ThemePreference.BronzeFallback, effective.Accent);
        Assert.Equal(AccentSource.GhostShellFallback, effective.AccentSource);
    }

    [Fact]
    public void Host_accessibility_snapshot_is_merged_with_Avalonia_colors()
    {
        var accessibility = new HostAccessibilityPreferences(
            reducedMotion: true,
            reducedTransparency: true,
            textScale: 1.5);

        var host = AvaloniaHostAppearanceAdapter.Map(
            PlatformThemeVariant.Dark,
            ColorContrastPreference.NoPreference,
            Color.FromRgb(0x24, 0x68, 0xAC),
            new HostPlatformCapabilities(
                HostOperatingSystem.Windows,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: false),
            accessibility);

        Assert.True(host.ReducedMotion);
        Assert.True(host.ReducedTransparency);
        Assert.Equal(1.5, host.TextScale);

        var effective = ThemePreference.Default.Resolve(host);
        Assert.False(effective.MotionEnabled);
        Assert.False(effective.AdvancedMaterialsEnabled);
        Assert.Equal(1.5, effective.TextScale);
    }

    [Fact]
    public void Platform_profile_produces_observable_metrics_and_classes()
    {
        var effective = new EffectiveTheme(
            EffectiveAppearanceMode.Light,
            PlatformProfile.Windows11,
            RgbColor.Parse("#0067C0"),
            AccentSource.Host,
            HighContrast: false,
            MotionEnabled: true,
            AdvancedMaterialsEnabled: true,
            TextScale: 1.25);

        var resources = EffectiveAppearanceResourceMapper.Map(effective);

        Assert.Equal("profile-windows11", resources.ProfileClass);
        Assert.Equal("appearance-light", resources.AppearanceClass);
        Assert.Equal(40, resources.ControlMinHeight);
        Assert.Equal(15, resources.BaseFontSize);
        Assert.Equal(1.25, resources.TextScale);
        Assert.Equal(31.25, resources.ScaleFontSize(25));
        Assert.Contains("Segoe UI", resources.FontFamily.Name, StringComparison.Ordinal);
        Assert.True(resources.AdvancedMaterialsEnabled);
        Assert.Contains("Windows11", resources.AppearanceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Application_text_scale_override_reaches_Avalonia_metrics()
    {
        var preference = new ThemePreference(
            new ThemePreferenceId("large-ui"),
            "Large UI",
            AppearanceMode.Dark,
            PlatformProfile.GhostShell,
            AccentPreference.GhostShellBronze,
            textScaleOverride: 2);
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            accent: null,
            textScale: 1.25);

        var resources = EffectiveAppearanceResourceMapper.Map(preference.Resolve(host));

        Assert.Equal(2, resources.TextScale);
        Assert.Equal(24, resources.BaseFontSize);
        Assert.Equal(50, resources.ScaleFontSize(25));
    }

    [Fact]
    public void Appearance_text_scale_options_cover_high_scale_and_preserve_imported_values()
    {
        Assert.Contains(
            MainWindow.AppearanceTextScaleOptions,
            option => option.Scale is null && option.DisplayName == "Follow host");
        Assert.Contains(
            MainWindow.AppearanceTextScaleOptions,
            option => option.Scale == 2 && option.DisplayName == "200%");
        Assert.Contains(
            MainWindow.AppearanceTextScaleOptions,
            option => option.Scale == 2.5 && option.DisplayName == "250%");

        var imported = MainWindow.ResolveApplicationTextScaleOption(1.3);

        Assert.Equal("130%", imported.DisplayName);
        Assert.Equal(1.3, imported.Scale);
    }

    [Fact]
    public void High_contrast_mapping_keeps_accent_legible_against_the_surface()
    {
        var effective = new EffectiveTheme(
            EffectiveAppearanceMode.Dark,
            PlatformProfile.GhostShell,
            RgbColor.Parse("#111111"),
            AccentSource.Custom,
            HighContrast: true,
            MotionEnabled: false,
            AdvancedMaterialsEnabled: false,
            TextScale: 1);

        var resources = EffectiveAppearanceResourceMapper.Map(effective);

        Assert.True(resources.HighContrast);
        Assert.False(resources.MotionEnabled);
        Assert.Equal(Colors.Black, resources.Background);
        Assert.True(
            EffectiveAppearanceResourceMapper.ContrastRatio(
                resources.Accent,
                resources.Background) >= 4.5);
        Assert.Contains("contrast adjusted", resources.AccentStatus, StringComparison.Ordinal);
    }
}
