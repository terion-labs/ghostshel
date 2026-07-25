using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Agent;

public sealed record AgentCompactionRequest(
    AgentRunId RunId,
    long Generation,
    ImmutableArray<AgentMessage> Messages);

public interface IAgentConversationCompactor
{
    ValueTask<AgentMessage> CompactAsync(
        AgentCompactionRequest request,
        CancellationToken cancellationToken);
}

public enum AgentCompactionErrorCode
{
    NothingToCompact,
    Busy,
    Cancelled,
    CompactorFailure,
    InvalidSummary,
    LimitExceeded,
    ConversationConflict,
}

public sealed record AgentCompactionResult
{
    private AgentCompactionResult(bool succeeded, AgentCompactionErrorCode? errorCode)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
    }

    public bool Succeeded { get; }

    public AgentCompactionErrorCode? ErrorCode { get; }

    internal static AgentCompactionResult Success() => new(true, null);

    internal static AgentCompactionResult Failure(AgentCompactionErrorCode errorCode) =>
        new(false, errorCode);
}
