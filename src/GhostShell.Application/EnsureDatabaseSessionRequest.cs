using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureDatabaseSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    DatabaseSessionTarget Target);
