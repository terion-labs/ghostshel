namespace GhostShell.Core;

public enum EffectiveAppearanceMode
{
    Light,
    Dark,
}

public enum AccentSource
{
    Custom,
    Host,
    GhostShellFallback,
}

public sealed record EffectiveTheme(
    EffectiveAppearanceMode Appearance,
    PlatformProfile PlatformProfile,
    RgbColor Accent,
    AccentSource AccentSource,
    bool HighContrast,
    bool MotionEnabled,
    bool AdvancedMaterialsEnabled,
    double TextScale,
    CornerStyle CornerStyle = CornerStyle.System,
    InterfaceDensity Density = InterfaceDensity.Cozy,
    bool ShowTabBar = true,
    bool ShowWorkspacesPanel = true,
    TabStripPlacement TabStripPlacement = TabStripPlacement.Top,
    WorkspacePanelPlacement WorkspacePanelPlacement = WorkspacePanelPlacement.Left);
