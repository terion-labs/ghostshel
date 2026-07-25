using GhostShell.Core;

namespace GhostShell.Application;

public sealed record ActorDescriptor(
    ActorId Id,
    ActorKind Kind,
    string DisplayName,
    ClientId? ClientId = null);
