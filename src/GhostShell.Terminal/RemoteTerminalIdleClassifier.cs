using GhostShell.Application;

namespace GhostShell.Terminal;

/// <summary>
/// Adds the close-safety information libghostty cannot derive when the host foreground process
/// permanently wraps another interactive shell. SSH keeps <c>ssh</c> in the foreground, and an
/// inline container shell keeps <c>docker exec</c> there, so Ghostty's semantic-prompt signal is
/// unavailable unless the wrapped shell happens to install integration.
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
                   StringComparison.OrdinalIgnoreCase) == true
               || launch.ShellActivityFallback == TerminalShellActivityFallback.PromptShape;
    }

    public static bool IsAtShellPrompt(
        string screen,
        int cursorRow,
        int cursorColumn,
        bool isAlternateScreen,
        bool isBracketedPasteEnabled,
        bool isMouseTrackingEnabled)
    {
        ArgumentNullException.ThrowIfNull(screen);
        // The alternate screen and mouse tracking mean something has taken the
        // terminal over, which is the opposite of sitting at a prompt.
        //
        // Bracketed paste does not, and treating it as though it did is what
        // made every modern remote shell look busy: bash and zsh turn it on at
        // the prompt precisely because that is where a paste needs protecting.
        // It is evidence of a shell waiting for input, not of a program using
        // the screen — and a program using the screen still has to fail the
        // prompt shape below to be called idle.
        _ = isBracketedPasteEnabled;
        if (isAlternateScreen
            || isMouseTrackingEnabled
            || cursorColumn <= 0)
        {
            return false;
        }

        var lines = screen.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var line = cursorRow >= 0 && cursorRow < lines.Length
            ? lines[cursorRow]
            : lines.LastOrDefault(candidate => candidate.Length > 0);
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var cursorText = line[..Math.Min(cursorColumn, line.Length)].TrimEnd();
        if (cursorText.Length == 0)
        {
            return false;
        }

        var prompt = cursorText[^1];
        var prefix = cursorText[..^1];
        var trimmedPrefix = prefix.TrimEnd();
        return prompt switch
        {
            '$' or '#' or '%' =>
                prefix.Length == 0
                || prefix.Contains('@', StringComparison.Ordinal)
                || prefix.EndsWith(']')
                || prefix.EndsWith(':')
                || (prefix.EndsWith(' ')
                    && (trimmedPrefix.StartsWith('/')
                        || trimmedPrefix.StartsWith('~'))),
            '>' =>
                prefix.StartsWith("PS ", StringComparison.OrdinalIgnoreCase)
                || prefix.Contains('@', StringComparison.Ordinal)
                || prefix.EndsWith(']'),
            _ => false,
        };
    }
}
