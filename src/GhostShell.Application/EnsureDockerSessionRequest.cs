using GhostShell.Core;

namespace GhostShell.Application;

public sealed record EnsureDockerSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    DockerSessionTarget Target);
