using GhostShell.Application;

namespace GhostShell.Terminal;

internal enum TerminalRuntimePlatform
{
    MacOs,
    Windows,
    Linux,
    Unsupported,
}

public static class TerminalSessionFactorySelector
{
    public static ITerminalSessionFactory CreateForCurrentPlatform() =>
        Create(DetectCurrentPlatform());

    internal static ITerminalSessionFactory Create(TerminalRuntimePlatform platform) => platform switch
    {
        TerminalRuntimePlatform.MacOs
            or TerminalRuntimePlatform.Windows
            or TerminalRuntimePlatform.Linux => new GhosttyVtTerminalSessionFactory(),
        TerminalRuntimePlatform.Unsupported => throw new PlatformNotSupportedException(
            "GhostSHELL terminal sessions support macOS, Windows, and Linux."),
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
    };

    private static TerminalRuntimePlatform DetectCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return TerminalRuntimePlatform.MacOs;
        }

        if (OperatingSystem.IsWindows())
        {
            return TerminalRuntimePlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return TerminalRuntimePlatform.Linux;
        }

        return TerminalRuntimePlatform.Unsupported;
    }
}
