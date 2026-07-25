using GhostShell.Core;

namespace GhostShell.Application;

public sealed record OperationContext(
    RequestId RequestId,
    ActorDescriptor Actor,
    long? ExpectedRevision = null,
    IdempotencyKey? IdempotencyKey = null,
    CancellationId? CancellationId = null,
    DateTimeOffset? DeadlineUtc = null)
{
    public static OperationContext ForHuman(
        ClientId clientId,
        long? expectedRevision = null,
        IdempotencyKey? idempotencyKey = null,
        DateTimeOffset? deadlineUtc = null) =>
        new(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId(clientId.Value),
                ActorKind.Human,
                "Local user",
                clientId),
            expectedRevision,
            idempotencyKey,
            GhostShell.Core.CancellationId.New(),
            deadlineUtc);
}
