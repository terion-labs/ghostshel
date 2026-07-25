using GhostShell.Core;

namespace GhostShell.Protocol;

public sealed record ProtocolWatchRequest(
    SessionId SessionId,
    long AfterSequence,
    int MaximumBatchSize);
