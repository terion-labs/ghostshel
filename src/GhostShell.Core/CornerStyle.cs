namespace GhostShell.Core;

/// <summary>
/// How rounded the shell is, as a character rather than a number.
///
/// This was a single radius applied to everything, which cannot be right: one
/// value has to stand for a window's base surface and for a segmented button,
/// and no value is good for both. Twenty-six is right for the outermost
/// surface on this desktop and absurd on a control; eight is the reverse.
///
/// The platform profile still says what each role is worth, and this scales
/// that whole set at once, so the relationships between them survive being
/// adjusted. A softer shell is rounder everywhere in proportion, not flattened
/// to one number.
/// </summary>
public enum CornerStyle
{
    /// <summary>The platform profile's own radii, unscaled.</summary>
    System = 0,

    /// <summary>Tighter throughout, for a shell that reads as squarer.</summary>
    Sharp = 1,

    /// <summary>Rounder throughout, following the platform's newer chrome.</summary>
    Soft = 2,
}

/// <summary>
/// What each corner style does to the profile's radii.
/// </summary>
public static class CornerStyleScale
{
    public static double For(CornerStyle style) => style switch
    {
        CornerStyle.Sharp => 0.5,
        CornerStyle.Soft => 1.75,
        _ => 1,
    };
}
