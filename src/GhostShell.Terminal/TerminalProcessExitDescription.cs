using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

/// <summary>
/// Converts the final screen of a wrapped connection process into bounded presentation text.
/// Raw terminal content and endpoint values never leave this classifier.
/// </summary>
internal static class TerminalProcessExitDescription
{
    private const int MaximumClassifiedScreenCharacters = 4 * 1024;

    public static string Describe(
        TerminalLaunchRequest launch,
        string screen,
        int? exitCode)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(screen);
        if (!IsSsh(launch))
        {
            return ExitCode("terminal", exitCode);
        }

        if (exitCode is null or 0)
        {
            return exitCode == 0
                ? "The SSH session ended normally."
                : "The SSH session ended.";
        }

        var recentScreen = screen.Length <= MaximumClassifiedScreenCharacters
            ? screen
            : screen[^MaximumClassifiedScreenCharacters..];
        var error = ConnectionRuntimeError.ClassifyProcessFailure(
            ConnectionKind.Ssh,
            recentScreen);
        return error.Code == ConnectionRuntimeErrorCode.ProcessFailed
            ? ExitCode("OpenSSH", exitCode)
            : error.Message;
    }

    private static bool IsSsh(TerminalLaunchRequest launch) =>
        launch.ConnectionMetadata?.ConnectionBoundary.StartsWith(
            "SSH:",
            StringComparison.OrdinalIgnoreCase) == true;

    private static string ExitCode(string process, int? exitCode) =>
        exitCode is { } known
            ? $"The {process} process exited with code {known}."
            : $"The {process} process exited.";
}
