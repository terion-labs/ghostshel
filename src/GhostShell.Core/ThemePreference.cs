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

/// <summary>Interface density, which scales control padding and heights.</summary>
public enum InterfaceDensity
{
    Compact,
    Cozy,
    Comfortable,
}

/// <summary>Which edge the tab strip sits on.</summary>
public enum TabStripPlacement
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>Which edge the workspaces rail docks to.</summary>
public enum WorkspacePanelPlacement
{
    Left,
    Right,
}

public sealed record ThemePreference : IDurableDefinition
{
    /// <summary>
    /// Version 2 adds the window-chrome settings. Every one of them is optional
    /// with a defined default, so a stored version 1 document still deserializes.
    /// </summary>
    // Not moved for the translucency change. A stored payload that still
    // carries a blur radius reads correctly: the property is ignored and the
    // switch takes its default. Moving it would have every saved theme fail
    // validation as an unsupported schema and be replaced by defaults, which
    // is how a corner radius someone had chosen came back as the fallback.
    public const int CurrentSchemaVersion = 2;

    public const double MinimumCornerRadius = 0;

    // Twenty could not express what the platform itself now uses. macOS 26
    // rounds windows far harder than that, and concentrically — the radius
    // follows whatever sits at the top of the window, so there is no single
    // published number to copy and this has to be reachable rather than
    // guessed at.
    public const double MaximumCornerRadius = 32;

    /// <summary>Whether the shell sits on a translucent base surface at all.</summary>
    public const bool DefaultIsTranslucent = true;

    /// <summary>
    /// How solid the shell's base surface is, as a percentage. The blur is
    /// only half of glass; the other half is how much of the blurred desktop
    /// is allowed through. Near 100 the surface reads as a painted frame
    /// around the panels rather than as a material, which is the difference
    /// between a dark gutter and a window you can see into.
    /// </summary>
    public const int MinimumBackdropOpacityPercent = 40;

    public const int MaximumBackdropOpacityPercent = 100;

    // Seventy-eight is where the dark shell sits against the platform's own
    // glass rather than in front of it.
    public const int DefaultBackdropOpacityPercent = 78;

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
        double? textScaleOverride = null,
        double? cornerRadiusOverride = null,
        InterfaceDensity density = InterfaceDensity.Cozy,
        bool showTabBar = true,
        bool showWorkspacesPanel = true,
        TabStripPlacement tabStripPlacement = TabStripPlacement.Top,
        WorkspacePanelPlacement workspacePanelPlacement = WorkspacePanelPlacement.Left,
        bool isTranslucent = DefaultIsTranslucent,
        int backdropOpacityPercent = DefaultBackdropOpacityPercent)
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

        if (cornerRadiusOverride is { } radius
            && (!double.IsFinite(radius)
                || radius < MinimumCornerRadius
                || radius > MaximumCornerRadius))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cornerRadiusOverride),
                cornerRadiusOverride,
                $"Corner radius must be between {MinimumCornerRadius} and {MaximumCornerRadius}.");
        }

        if (backdropOpacityPercent is < MinimumBackdropOpacityPercent
            or > MaximumBackdropOpacityPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backdropOpacityPercent),
                backdropOpacityPercent,
                $"Backdrop opacity must be between {MinimumBackdropOpacityPercent} and "
                + $"{MaximumBackdropOpacityPercent}.");
        }

        if (!Enum.IsDefined(density))
        {
            throw new ArgumentOutOfRangeException(nameof(density), density, "Unknown density.");
        }

        if (!Enum.IsDefined(tabStripPlacement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tabStripPlacement),
                tabStripPlacement,
                "Unknown tab placement.");
        }

        if (!Enum.IsDefined(workspacePanelPlacement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspacePanelPlacement),
                workspacePanelPlacement,
                "Unknown workspace-panel placement.");
        }

        Id = id;
        Name = name;
        Appearance = appearance;
        PlatformProfile = platformProfile;
        Accent = accent;
        TextScaleOverride = textScaleOverride;
        CornerRadiusOverride = cornerRadiusOverride;
        Density = density;
        ShowTabBar = showTabBar;
        ShowWorkspacesPanel = showWorkspacesPanel;
        TabStripPlacement = tabStripPlacement;
        WorkspacePanelPlacement = workspacePanelPlacement;
        IsTranslucent = isTranslucent;
        BackdropOpacityPercent = backdropOpacityPercent;
    }

    public static DefinitionKind Kind => DefinitionKind.Theme;

    public ThemePreferenceId Id { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public string Name { get; }

    public AppearanceMode Appearance { get; }

    public PlatformProfile PlatformProfile { get; }

    public AccentPreference Accent { get; }

    public double? TextScaleOverride { get; }

    /// <summary>Null follows the platform profile's own radius.</summary>
    public double? CornerRadiusOverride { get; }

    public InterfaceDensity Density { get; }

    public bool ShowTabBar { get; }

    public bool ShowWorkspacesPanel { get; }

    public TabStripPlacement TabStripPlacement { get; }

    public WorkspacePanelPlacement WorkspacePanelPlacement { get; }

    /// <summary>
    /// How far the desktop is blurred behind the base surface. Zero turns the
    /// translucency off with it — an opaque shell has nothing to blur behind.
    /// </summary>
    /// <summary>
    /// Whether the shell sits on a translucent base surface.
    ///
    /// This was a blur radius, which only ever meant pixels because macOS was
    /// blurred by an explicit radius underneath. The shell now hands the
    /// platform its own material, and every platform's is a capability rather
    /// than a number — so the only part of that setting which still decided
    /// anything was whether it was zero.
    /// </summary>
    public bool IsTranslucent { get; }

    /// <summary>How solid the base surface is, as a percentage.</summary>
    public int BackdropOpacityPercent { get; }

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

        // Transparency effects follow the host's own reduced-transparency and
        // high-contrast preferences, so an accessibility setting is never
        // overridden by a stored profile.
        return new EffectiveTheme(
            appearance,
            platformProfile,
            accent,
            source,
            host.HighContrast,
            !host.ReducedMotion,
            materialsEnabled,
            TextScaleOverride ?? host.TextScale,
            CornerRadiusOverride,
            Density,
            ShowTabBar,
            ShowWorkspacesPanel,
            TabStripPlacement,
            WorkspacePanelPlacement);
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
