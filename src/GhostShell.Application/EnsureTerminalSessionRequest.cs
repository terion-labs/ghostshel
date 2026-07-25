using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureTerminalSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    TerminalLaunchRequest Launch);
