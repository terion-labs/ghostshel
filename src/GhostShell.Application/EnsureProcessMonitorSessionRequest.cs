using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureProcessMonitorSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title);
