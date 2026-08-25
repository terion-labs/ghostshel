namespace GhostShell.Application;

/// <summary>
/// Canonical identity shown by the application and exported in support evidence.
/// The macOS packaging gate checks these values against the reviewed artwork manifest
/// and Info.plist before it can publish a bundle.
/// </summary>
public static class ProductIdentity
{
    public const string DisplayName = "GhostSHELL";

    public const string ExecutableName = "GhostShell";

    public const string BundleIdentifier = "app.ghostshell";
}
