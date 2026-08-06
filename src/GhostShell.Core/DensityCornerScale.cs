namespace GhostShell.Core;

/// <summary>
/// What the interface density does to the shell's corners.
///
/// Corners and spacing were two settings, which let them disagree: a shell
/// could be spacious and sharp at once, and the radius a surface earns depends
/// on how far it stands off from the one around it. They are one axis now, and
/// this is the half of it that curves.
///
/// The platform profile still says what each role is worth. This scales that
/// whole set, so the distances between roles survive being adjusted rather
/// than collapsing to one number.
/// </summary>
public static class DensityCornerScale
{
    public static double For(InterfaceDensity density) => density switch
    {
        InterfaceDensity.Compact => 0.5,
        InterfaceDensity.Comfortable => 1.75,
        _ => 1,
    };

    /// <summary>
    /// Which kind of window to ask to be. This desktop draws a window's own
    /// corners at one of three radii by what kind of window it is, so the
    /// setting picks the kind rather than a number.
    /// </summary>
    public static double WindowRadius(InterfaceDensity density) => density switch
    {
        InterfaceDensity.Compact => 16,
        InterfaceDensity.Comfortable => 26,
        _ => 20,
    };
}
