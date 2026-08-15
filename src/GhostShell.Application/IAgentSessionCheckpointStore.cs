using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AgentSessionCheckpointSummary(
    AgentRunId RunId,
    int SchemaVersion,
    long Generation,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable storage for versioned, idle native-agent checkpoints. This port
/// deliberately stores no provider client, approval, authority, or secret
/// material; the kernel-owned payload is data only.
/// </summary>
public interface IAgentSessionCheckpointStore
{
    ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveAsync(
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>> LoadAsync(
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteAsync(
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<IReadOnlyList<AgentSessionCheckpointSummary>>>
        ListAsync(int maximumCount, CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveAsync(
        AgentConversationScopeId conversationScopeId,
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>> LoadAsync(
        AgentConversationScopeId conversationScopeId,
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteAsync(
        AgentConversationScopeId conversationScopeId,
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<IReadOnlyList<AgentSessionCheckpointSummary>>>
        ListAsync(
            AgentConversationScopeId conversationScopeId,
            int maximumCount,
            CancellationToken cancellationToken);
}
