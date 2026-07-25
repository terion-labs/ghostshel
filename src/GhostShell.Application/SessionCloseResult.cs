using GhostShell.Core;

namespace GhostShell.Application;

public sealed record SessionCloseResult(
    SessionId SessionId,
    SessionCloseOutcome Outcome,
    string Detail);
