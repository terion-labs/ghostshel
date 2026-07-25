using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

internal sealed class AvaloniaHostAppearanceAdapter
{
    private readonly IPlatformSettings _platformSettings;
    private readonly HostPlatformCapabilities _capabilities;
    private readonly IHostAccessibilityPreferencesSource? _accessibilityPreferences;

    public AvaloniaHostAppearanceAdapter(IPlatformSettings platformSettings)
        : this(platformSettings, HostPlatformCapabilities.Detect())
    {
    }

    public AvaloniaHostAppearanceAdapter(
        IPlatformSettings platformSettings,
        IHostAccessibilityPreferencesSource accessibilityPreferences)
        : this(
            platformSettings,
            HostPlatformCapabilities.Detect(),
            accessibilityPreferences)
    {
    }

    internal AvaloniaHostAppearanceAdapter(
        IPlatformSettings platformSettings,
        HostPlatformCapabilities capabilities,
        IHostAccessibilityPreferencesSource? accessibilityPreferences = null)
    {
        _platformSettings = platformSettings
            ?? throw new ArgumentNullException(nameof(platformSettings));
        _capabilities = capabilities;
        _accessibilityPreferences = accessibilityPreferences;
    }

    public HostAppearance GetCurrent()
    {
        var colors = _platformSettings.GetColorValues();
        return Map(
            colors.ThemeVariant,
            colors.ContrastPreference,
            colors.AccentColor1,
            _capabilities,
            _accessibilityPreferences?.Current ?? HostAccessibilityPreferences.Default);
    }

    internal static HostAppearance Map(
        PlatformThemeVariant themeVariant,
        ColorContrastPreference contrastPreference,
        Color accent,
        HostPlatformCapabilities capabilities) => Map(
            themeVariant,
            contrastPreference,
            accent,
            capabilities,
            HostAccessibilityPreferences.Default);

    internal static HostAppearance Map(
        PlatformThemeVariant themeVariant,
        ColorContrastPreference contrastPreference,
        Color accent,
        HostPlatformCapabilities capabilities,
        HostAccessibilityPreferences accessibilityPreferences)
    {
        ArgumentNullException.ThrowIfNull(accessibilityPreferences);
        var colorScheme = themeVariant == PlatformThemeVariant.Light
            ? HostColorScheme.Light
            : HostColorScheme.Dark;
        RgbColor? hostAccent = accent.A == 0
            ? null
            : new RgbColor(accent.R, accent.G, accent.B);

        return new HostAppearance(
            capabilities.OperatingSystem,
            colorScheme,
            hostAccent,
            capabilities.LinuxDesktop,
            highContrast: contrastPreference != ColorContrastPreference.NoPreference,
            reducedMotion: accessibilityPreferences.ReducedMotion,
            reducedTransparency: accessibilityPreferences.ReducedTransparency,
            textScale: accessibilityPreferences.TextScale,
            supportsAdvancedMaterials: capabilities.SupportsAdvancedMaterials,
            supportsLiquidGlass: capabilities.SupportsLiquidGlass);
    }
}

internal sealed record HostPlatformCapabilities(
    HostOperatingSystem OperatingSystem,
    LinuxDesktopEnvironment LinuxDesktop,
    bool SupportsAdvancedMaterials,
    bool SupportsLiquidGlass)
{
    public static HostPlatformCapabilities Detect()
    {
        if (System.OperatingSystem.IsMacOS())
        {
            return new HostPlatformCapabilities(
                HostOperatingSystem.MacOS,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: System.OperatingSystem.IsMacOSVersionAtLeast(26));
        }

        if (System.OperatingSystem.IsWindows())
        {
            return new HostPlatformCapabilities(
                HostOperatingSystem.Windows,
                LinuxDesktopEnvironment.Unknown,
                SupportsAdvancedMaterials: true,
                SupportsLiquidGlass: false);
        }

        return new HostPlatformCapabilities(
            HostOperatingSystem.Linux,
            DetectLinuxDesktop(
                Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
                Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"),
                Environment.GetEnvironmentVariable("DESKTOP_SESSION")),
            SupportsAdvancedMaterials: false,
            SupportsLiquidGlass: false);
    }

    internal static LinuxDesktopEnvironment DetectLinuxDesktop(params string?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var description = string.Join(':', values.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (description.Contains("KDE", StringComparison.OrdinalIgnoreCase)
            || description.Contains("PLASMA", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxDesktopEnvironment.Kde;
        }

        if (description.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
        {
            return LinuxDesktopEnvironment.Gnome;
        }

        return LinuxDesktopEnvironment.Unknown;
    }
}

internal sealed record EffectiveAppearanceResources(
    ThemeVariant ThemeVariant,
    string ProfileClass,
    string AppearanceClass,
    Color Background,
    Color Surface,
    Color RaisedSurface,
    Color HoverSurface,
    Color Border,
    Color Text,
    Color MutedText,
    Color Accent,
    Color AccentForeground,
    Color AccentSoft,
    Color Danger,
    Color DangerForeground,
    Color DangerSoft,
    Color DangerBorder,
    Color Success,
    Color SuccessSoft,
    Color SuccessBorder,
    Color Warning,
    Color WarningSoft,
    Color WarningBorder,
    Color NoticeBorder,
    FontFamily FontFamily,
    double BaseFontSize,
    double TextScale,
    double ControlMinHeight,
    CornerRadius ControlCornerRadius,
    Thickness ControlPadding,
    CornerRadius CardCornerRadius,
    string AppearanceStatus,
    string AccentStatus,
    bool HighContrast,
    bool MotionEnabled,
    bool AdvancedMaterialsEnabled)
{
    internal double ScaleFontSize(double baseFontSize)
    {
        if (!double.IsFinite(baseFontSize) || baseFontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseFontSize),
                baseFontSize,
                "A base font size must be finite and greater than zero.");
        }

        return baseFontSize * TextScale;
    }
}

internal static class EffectiveAppearanceResourceMapper
{
    public static EffectiveAppearanceResources Map(EffectiveTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var isLight = theme.Appearance == EffectiveAppearanceMode.Light;
        var background = Parse(isLight ? "#F2F3F0" : "#111111");
        var surface = Parse(isLight ? "#E7E8E5" : "#18181B");
        var raised = Parse(isLight ? "#FFFFFF" : "#1A1A1A");
        var hover = Parse(isLight ? "#D7D8D5" : "#2E2E2E");
        var border = Parse(isLight ? "#B8BAB6" : "#38383C");
        var text = Parse(isLight ? "#111111" : "#FFFFFF");
        var muted = Parse(isLight ? "#5C5E5A" : "#B8B9B6");

        if (theme.HighContrast)
        {
            background = Parse(isLight ? "#FFFFFF" : "#000000");
            surface = Parse(isLight ? "#FFFFFF" : "#000000");
            raised = Parse(isLight ? "#FFFFFF" : "#080808");
            hover = Parse(isLight ? "#E6E6E6" : "#202020");
            border = Parse(isLight ? "#000000" : "#FFFFFF");
            text = Parse(isLight ? "#000000" : "#FFFFFF");
            muted = Parse(isLight ? "#262626" : "#E6E6E6");
        }

        var requestedAccent = ToColor(theme.Accent);
        var accent = EnsureContrast(
            requestedAccent,
            background,
            theme.HighContrast ? 4.5 : 3.0,
            isLight ? Colors.Black : Colors.White);
        var accentForeground = ContrastRatio(accent, Colors.Black)
            >= ContrastRatio(accent, Colors.White)
            ? Colors.Black
            : Colors.White;
        var accentSoft = Blend(accent, background, theme.HighContrast ? 0.24 : 0.18);
        var metrics = PlatformMetrics.For(theme.PlatformProfile);
        var source = theme.AccentSource switch
        {
            AccentSource.Custom => "custom accent",
            AccentSource.Host => "host accent",
            _ => "GhostSHELL bronze fallback",
        };

        var danger = Parse(isLight ? "#B42318" : "#FF7B72");
        var dangerForeground = ContrastRatio(danger, Colors.Black)
            >= ContrastRatio(danger, Colors.White)
            ? Colors.Black
            : Colors.White;

        return new EffectiveAppearanceResources(
            isLight ? ThemeVariant.Light : ThemeVariant.Dark,
            ProfileClass(theme.PlatformProfile),
            isLight ? "appearance-light" : "appearance-dark",
            background,
            surface,
            raised,
            hover,
            border,
            text,
            muted,
            accent,
            accentForeground,
            accentSoft,
            danger,
            dangerForeground,
            Parse(isLight ? "#FDE8E5" : "#3A1715"),
            Parse(isLight ? "#A73528" : "#A84C45"),
            Parse(isLight ? "#147A3F" : "#77D797"),
            Parse(isLight ? "#E2F5E8" : "#173222"),
            Parse(isLight ? "#41935E" : "#2E6743"),
            Parse(isLight ? "#8A4B08" : "#E1A45F"),
            Parse(isLight ? "#FFF0D6" : "#32241A"),
            Parse(isLight ? "#B46B20" : "#75502F"),
            Blend(accent, border, 0.55),
            new FontFamily(metrics.FontFamily),
            12 * theme.TextScale,
            theme.TextScale,
            metrics.ControlMinHeight * theme.TextScale,
            new CornerRadius(metrics.ControlCornerRadius),
            new Thickness(
                metrics.HorizontalPadding * theme.TextScale,
                metrics.VerticalPadding * theme.TextScale),
            new CornerRadius(metrics.CardCornerRadius),
            $"Effective: {theme.PlatformProfile} · {(isLight ? "Light" : "Dark")}" +
            (theme.HighContrast ? " · High contrast" : string.Empty),
            $"Accent: {source}" +
            (accent == requestedAccent ? string.Empty : " · contrast adjusted"),
            theme.HighContrast,
            theme.MotionEnabled,
            theme.AdvancedMaterialsEnabled);
    }

    internal static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static Color EnsureContrast(
        Color candidate,
        Color background,
        double minimumRatio,
        Color target)
    {
        if (ContrastRatio(candidate, background) >= minimumRatio)
        {
            return candidate;
        }

        for (var step = 1; step <= 20; step++)
        {
            var adjusted = Blend(target, candidate, step / 20d);
            if (ContrastRatio(adjusted, background) >= minimumRatio)
            {
                return adjusted;
            }
        }

        return target;
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R / 255d)
        + 0.7152 * Linearize(color.G / 255d)
        + 0.0722 * Linearize(color.B / 255d);

    private static double Linearize(double component) => component <= 0.04045
        ? component / 12.92
        : Math.Pow((component + 0.055) / 1.055, 2.4);

    private static Color Blend(Color foreground, Color background, double foregroundAmount)
    {
        var amount = Math.Clamp(foregroundAmount, 0, 1);
        return Color.FromRgb(
            Blend(foreground.R, background.R, amount),
            Blend(foreground.G, background.G, amount),
            Blend(foreground.B, background.B, amount));
    }

    private static byte Blend(byte foreground, byte background, double foregroundAmount) =>
        checked((byte)Math.Round(
            foreground * foregroundAmount + background * (1 - foregroundAmount)));

    private static string ProfileClass(PlatformProfile profile) => profile switch
    {
        PlatformProfile.MacOsClassic => "profile-macos-classic",
        PlatformProfile.MacOsLiquidGlass => "profile-macos-liquid-glass",
        PlatformProfile.Windows11 => "profile-windows11",
        PlatformProfile.Gnome => "profile-gnome",
        PlatformProfile.Kde => "profile-kde",
        PlatformProfile.Custom => "profile-custom",
        _ => "profile-ghostshell",
    };

    private static Color Parse(string value) => Color.Parse(value);

    private static Color ToColor(RgbColor color) => Color.FromRgb(
        color.Red,
        color.Green,
        color.Blue);

    private sealed record PlatformMetrics(
        string FontFamily,
        double ControlMinHeight,
        double ControlCornerRadius,
        double HorizontalPadding,
        double VerticalPadding,
        double CardCornerRadius)
    {
        public static PlatformMetrics For(PlatformProfile profile) => profile switch
        {
            PlatformProfile.MacOsClassic => new(
                "SF Pro Text, Inter, sans-serif",
                28,
                7,
                10,
                6,
                10),
            PlatformProfile.MacOsLiquidGlass => new(
                "SF Pro Text, Inter, sans-serif",
                30,
                10,
                11,
                6,
                13),
            PlatformProfile.Windows11 => new(
                "Segoe UI Variable, Segoe UI, Inter, sans-serif",
                32,
                6,
                11,
                6,
                8),
            PlatformProfile.Gnome => new(
                "Cantarell, Inter, sans-serif",
                34,
                9,
                12,
                7,
                12),
            PlatformProfile.Kde => new(
                "Noto Sans, Inter, sans-serif",
                30,
                4,
                10,
                6,
                6),
            _ => new(
                "Inter, Segoe UI, sans-serif",
                30,
                7,
                10,
                6,
                9),
        };
    }
}
