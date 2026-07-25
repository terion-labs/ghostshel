using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureStatisticsSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title);
