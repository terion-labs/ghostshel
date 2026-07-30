using GhostShell.Application;

namespace GhostShell.Terminal;

/// <summary>
/// Adds the one piece of close-safety information libghostty cannot derive for an SSH surface.
/// The local foreground process remains <c>ssh</c> for the entire remote session, so Ghostty's
/// semantic-prompt signal is unavailable unless the remote shell happens to install integration.
/// This classifier is deliberately conservative: it recognizes only common shell prompt shapes
/// at the canonical cursor and otherwise preserves the confirmation.
/// </summary>
internal static class RemoteTerminalIdleClassifier
{
    public static bool AppliesTo(TerminalLaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return launch.ConnectionMetadata?.ConnectionBoundary.StartsWith(
            "SSH:",
            StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool IsAtShellPrompt(
        string screen,
        GhosttyTerminalScreenState state)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (state.IsAlternateScreen
            || state.IsBracketedPasteEnabled
            || state.IsMouseTrackingEnabled
            || state.CursorColumn <= 0)
        {
            return false;
        }

        var lines = screen.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var line = state.CursorRow < lines.Length
            ? lines[state.CursorRow]
            : lines.LastOrDefault(candidate => candidate.Length > 0);
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var cursorText = line[..Math.Min(state.CursorColumn, line.Length)].TrimEnd();
        if (cursorText.Length == 0)
        {
            return false;
        }

        var prompt = cursorText[^1];
        var prefix = cursorText[..^1];
        return prompt switch
        {
            '$' or '#' or '%' =>
                prefix.Length == 0
                || prefix.Contains('@', StringComparison.Ordinal)
                || prefix.EndsWith(']')
                || prefix.EndsWith(':'),
            '>' =>
                prefix.StartsWith("PS ", StringComparison.OrdinalIgnoreCase)
                || prefix.Contains('@', StringComparison.Ordinal)
                || prefix.EndsWith(']'),
            _ => false,
        };
    }
}
