using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// The closed set of terminal operations that can be prepared for agent authorization.
/// Provider-defined names and argument bags cannot extend this set. Mutation requests
/// carry execution material only; the trusted host acquires their one-action input lease.
/// </summary>
public abstract record AgentTerminalRequest
{
    private AgentTerminalRequest()
    {
    }

    public sealed record ReadScreen(SessionId SessionId) : AgentTerminalRequest;

    public sealed record SendText(
        SessionId SessionId,
        string Text) : AgentTerminalRequest;

    public sealed record Paste(
        SessionId SessionId,
        string Text) : AgentTerminalRequest;

    public sealed record SendKey(
        SessionId SessionId,
        TerminalKeyStroke KeyStroke) : AgentTerminalRequest;

    public sealed record SendChord(
        SessionId SessionId,
        TerminalCharacterChord Chord) : AgentTerminalRequest;

    public sealed record SendMouse(
        SessionId SessionId,
        TerminalMouseInput MouseInput) : AgentTerminalRequest;

    public sealed record WaitForText(TerminalWaitForTextRequest Value) : AgentTerminalRequest;

    public sealed record WaitForChange(TerminalWaitForChangeRequest Value) : AgentTerminalRequest;

    public sealed record WaitForStable(TerminalWaitForStableRequest Value) : AgentTerminalRequest;

    public sealed record Interrupt(SessionId SessionId) : AgentTerminalRequest;

    public sealed record Resize(TerminalResizeRequest Value) : AgentTerminalRequest;
}
