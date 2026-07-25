namespace GhostShell.App.Controls;

internal enum DesktopTerminalPlatform
{
    MacOs,
    Windows,
    Linux,
    Unsupported,
}

internal enum TerminalPresentationKind
{
    Native,
    Managed,
}

internal static class TerminalPresentationSelector
{
    public static TerminalPresentationKind SelectCurrent() => Select(DetectCurrent());

    public static TerminalPresentationKind Select(DesktopTerminalPlatform platform) => platform switch
    {
        DesktopTerminalPlatform.MacOs => TerminalPresentationKind.Native,
        DesktopTerminalPlatform.Windows or DesktopTerminalPlatform.Linux =>
            TerminalPresentationKind.Managed,
        DesktopTerminalPlatform.Unsupported => throw new PlatformNotSupportedException(
            "GhostSHELL terminal presentation supports macOS, Windows, and Linux."),
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
    };

    private static DesktopTerminalPlatform DetectCurrent()
    {
        if (OperatingSystem.IsMacOS())
        {
            return DesktopTerminalPlatform.MacOs;
        }

        if (OperatingSystem.IsWindows())
        {
            return DesktopTerminalPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return DesktopTerminalPlatform.Linux;
        }

        return DesktopTerminalPlatform.Unsupported;
    }
}
