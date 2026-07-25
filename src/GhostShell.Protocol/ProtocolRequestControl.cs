using GhostShell.Core;

namespace GhostShell.Protocol;

public sealed record ProtocolRequestControl(
    CancellationId? CancellationId,
    DateTimeOffset? DeadlineUtc);
