using System.Text.Json.Serialization;

namespace GhostShell.Core;

public enum AppearanceMode
{
    System,
    Light,
    Dark,
}

public enum PlatformProfile
{
    Automatic,
    MacOsClassic,
    MacOsLiquidGlass,
    Windows11,
    Gnome,
    Kde,
    GhostShell,
    Custom,
}

public sealed record ThemePreference : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    public static RgbColor BronzeFallback { get; } = RgbColor.Parse("#B8793A");

    public static ThemePreference Default { get; } = new(
        new ThemePreferenceId("builtin.theme.automatic"),
        "Automatic",
        AppearanceMode.System,
        PlatformProfile.Automatic,
        AccentPreference.FollowHost);

    public ThemePreference(
        ThemePreferenceId id,
        string name,
        AppearanceMode appearance,
        PlatformProfile platformProfile,
        AccentPreference accent,
        double? textScaleOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(accent);
        if (textScaleOverride is { } scale
            && (!double.IsFinite(scale) || scale is < 0.5 or > 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(textScaleOverride),
                textScaleOverride,
                "Application text scale must be between 0.5 and 4.");
        }

        Id = id;
        Name = name;
        Appearance = appearance;
        PlatformProfile = platformProfile;
        Accent = accent;
        TextScaleOverride = textScaleOverride;
    }

    public static DefinitionKind Kind => DefinitionKind.Theme;

    public ThemePreferenceId Id { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public string Name { get; }

    public AppearanceMode Appearance { get; }

    public PlatformProfile PlatformProfile { get; }

    public AccentPreference Accent { get; }

    public double? TextScaleOverride { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public EffectiveTheme Resolve(HostAppearance host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var appearance = Appearance switch
        {
            AppearanceMode.Light => EffectiveAppearanceMode.Light,
            AppearanceMode.Dark => EffectiveAppearanceMode.Dark,
            AppearanceMode.System when host.ColorScheme == HostColorScheme.Light => EffectiveAppearanceMode.Light,
            _ => EffectiveAppearanceMode.Dark,
        };

        var platformProfile = ResolvePlatformProfile(host);
        var (accent, source) = ResolveAccent(host);
        var materialsEnabled = host.SupportsAdvancedMaterials
            && !host.HighContrast
            && !host.ReducedTransparency;

        return new EffectiveTheme(
            appearance,
            platformProfile,
            accent,
            source,
            host.HighContrast,
            !host.ReducedMotion,
            materialsEnabled,
            TextScaleOverride ?? host.TextScale);
    }

    private PlatformProfile ResolvePlatformProfile(HostAppearance host)
    {
        if (PlatformProfile == PlatformProfile.MacOsLiquidGlass && !host.SupportsLiquidGlass)
        {
            return PlatformProfile.MacOsClassic;
        }

        if (PlatformProfile != PlatformProfile.Automatic)
        {
            return PlatformProfile;
        }

        return host.OperatingSystem switch
        {
            HostOperatingSystem.MacOS when host.SupportsLiquidGlass => PlatformProfile.MacOsLiquidGlass,
            HostOperatingSystem.MacOS => PlatformProfile.MacOsClassic,
            HostOperatingSystem.Windows => PlatformProfile.Windows11,
            HostOperatingSystem.Linux when host.LinuxDesktop == LinuxDesktopEnvironment.Gnome => PlatformProfile.Gnome,
            HostOperatingSystem.Linux when host.LinuxDesktop == LinuxDesktopEnvironment.Kde => PlatformProfile.Kde,
            _ => PlatformProfile.GhostShell,
        };
    }

    private (RgbColor Color, AccentSource Source) ResolveAccent(HostAppearance host) => Accent.Kind switch
    {
        AccentPreferenceKind.Custom => (Accent.CustomColor!.Value, AccentSource.Custom),
        AccentPreferenceKind.FollowHost when host.Accent is { } hostAccent => (hostAccent, AccentSource.Host),
        _ => (BronzeFallback, AccentSource.GhostShellFallback),
    };
}
