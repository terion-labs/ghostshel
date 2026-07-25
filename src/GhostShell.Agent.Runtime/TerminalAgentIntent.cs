using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal abstract record TerminalAgentIntent
{
    private TerminalAgentIntent()
    {
    }

    public sealed record ReadScreen : TerminalAgentIntent;

    public sealed record SendText(string Text) : TerminalAgentIntent;

    public sealed record Paste(string Text) : TerminalAgentIntent;

    public sealed record SendKey(TerminalKeyStroke KeyStroke) : TerminalAgentIntent;

    public sealed record SendChord(TerminalCharacterChord Chord) : TerminalAgentIntent;

    public sealed record SendMouse(TerminalMouseInput MouseInput) : TerminalAgentIntent;

    public sealed record WaitForText(string Text, TimeSpan Timeout)
        : TerminalAgentIntent;

    public sealed record WaitForChange(
        long AfterContentRevision,
        TimeSpan Timeout)
        : TerminalAgentIntent;

    public sealed record WaitForStable(
        TimeSpan StableFor,
        TimeSpan Timeout)
        : TerminalAgentIntent;

    public sealed record Interrupt : TerminalAgentIntent;

    public sealed record Resize(int Columns, int Rows) : TerminalAgentIntent;
}

internal abstract record TerminalAgentIntentResult
{
    private TerminalAgentIntentResult()
    {
    }

    public sealed record Parsed(
        TerminalAgentIntent Intent,
        PanelInstanceId? PanelId = null)
        : TerminalAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : TerminalAgentIntentResult;
}
