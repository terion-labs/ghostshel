using GhostShell.Core;

namespace GhostShell.Protocol;

public sealed record ProtocolActor(
    ActorId Id,
    string Kind,
    string DisplayName,
    ClientId? ClientId);
