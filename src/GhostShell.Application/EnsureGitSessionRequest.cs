using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureGitSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    GitSessionTarget Target);
