using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace GhostShell.Agent;

public sealed partial class NativeAgentSession
{
    private long _lastSubmittedToolGeneration;

    internal ValueTask<AgentTurnResult> SubmitToolResultsAsync(
        long proposalGeneration,
        ImmutableArray<AgentToolResult> results,
        ImmutableArray<AgentToolDefinition> tools,
        IAgentProvider provider,
        CancellationToken cancellationToken) =>
        SubmitToolResultsAsync(
            proposalGeneration,
            results,
            tools,
            tools,
            provider,
            cancellationToken);

    /// <summary>
    /// Commits results against the exact tool manifest that produced the
    /// pending proposals, then starts the continuation with a separately
    /// validated manifest. This is the only manifest-change boundary inside a
    /// structured provider turn.
    /// </summary>
    public async ValueTask<AgentTurnResult> SubmitToolResultsAsync(
        long proposalGeneration,
        ImmutableArray<AgentToolResult> results,
        ImmutableArray<AgentToolDefinition> proposalTools,
        ImmutableArray<AgentToolDefinition> continuationTools,
        IAgentProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(proposalGeneration);
        ArgumentNullException.ThrowIfNull(provider);
        if (results.IsDefault)
        {
            throw new ArgumentException(
                "The tool-result collection is required.",
                nameof(results));
        }

        if (proposalTools.IsDefault)
        {
            throw new ArgumentException(
                "The proposal tool collection is required.",
                nameof(proposalTools));
        }

        if (continuationTools.IsDefault)
        {
            throw new ArgumentException(
                "The continuation tool collection is required.",
                nameof(continuationTools));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
        }

        Dictionary<string, string> proposalToolNamesByProviderName;
        Dictionary<string, string> continuationToolNamesByProviderName;
        try
        {
            proposalToolNamesByProviderName = ValidateTools(proposalTools);
            continuationToolNamesByProviderName = ValidateTools(continuationTools);
            ValidateToolResultBounds(results);
        }
        catch (AgentLimitException)
        {
            return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
        }

        ActiveTurn activeTurn;
        AgentProviderRequest request;
        lock (_gate)
        {
            if (_activeTurn is not null)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.AlreadyRunning);
            }

            if (_pendingToolProposals.Length == 0)
            {
                var isStale = proposalGeneration <= _lastSubmittedToolGeneration
                    || proposalGeneration < _generation;
                return AgentTurnResult.Failure(
                    isStale
                        ? AgentTurnErrorCode.StaleToolResults
                        : AgentTurnErrorCode.NoPendingToolDecision);
            }

            var pendingTurn = _pendingToolTurn
                ?? throw new InvalidOperationException(
                    "Pending tool proposals must retain their continuation context.");
            var pendingGeneration = _pendingToolProposals[0].Generation;
            if (proposalGeneration != pendingGeneration)
            {
                return AgentTurnResult.Failure(
                    proposalGeneration < pendingGeneration
                        ? AgentTurnErrorCode.StaleToolResults
                        : AgentTurnErrorCode.ToolResultMismatch);
            }

            if (!ToolDefinitionsEqual(pendingTurn.Tools, proposalTools)
                || !ToolResultsMatchPendingProposals(results))
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.ToolResultMismatch);
            }

            if (_providerOperationsInFlight >= _limits.MaximumConcurrentProviderOperations)
            {
                return AgentTurnResult.Failure(
                    AgentTurnErrorCode.ProviderOperationLimit);
            }

            if (_conversation.Length
                > _limits.MaximumConversationMessages - results.Length - 1)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }

            ImmutableArray<AgentMessage> conversationWithResults;
            try
            {
                conversationWithResults = _conversation.AddRange(
                    results.Select(AgentMessage.FromToolResult));
                ValidateConversation(
                    conversationWithResults,
                    ConversationTail.ToolResults);
            }
            catch (AgentLimitException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }
            catch (AgentConversationException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.ToolResultMismatch);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            try
            {
                RegisterProviderToolBindings(
                    proposalToolNamesByProviderName.Concat(
                        continuationToolNamesByProviderName));
            }
            catch (AgentLimitException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }
            catch (AgentConversationException exception)
            {
                throw new ArgumentException(
                    "A provider tool alias cannot be rebound within a session.",
                    nameof(continuationTools),
                    exception);
            }

            var generation = checked(_generation + 1);
            var nextConversationRevision = checked(_conversationRevision + 1);
            // This is the submission linearization point. Once the exact results enter the
            // transcript, cancellation and provider failure may fence the turn but never erase it.
            _generation = generation;
            _conversation = conversationWithResults;
            _conversationRevision = nextConversationRevision;
            _lastSubmittedToolGeneration = proposalGeneration;
            _pendingToolTurn = null;
            _pendingToolProposals = [];
            activeTurn = new ActiveTurn(
                generation,
                nextConversationRevision,
                conversationWithResults,
                [],
                continuationTools,
                pendingTurn.ReasoningEffort,
                ActiveTurnKind.ToolContinuation);
            _activeTurn = activeTurn;
            _providerOperationsInFlight = checked(_providerOperationsInFlight + 1);
            _state = NativeAgentSessionState.Streaming;
            request = new AgentProviderRequest(
                RunId,
                generation,
                conversationWithResults,
                continuationTools,
                pendingTurn.ReasoningEffort);
            AppendEventUnsafe(AgentRunEventKind.TurnStarted, generation);
        }

        return await ExecuteProviderTurnAsync(
            activeTurn,
            request,
            continuationToolNamesByProviderName,
            provider,
            cancellationToken).ConfigureAwait(false);
    }

    private void ValidateToolResultBounds(ImmutableArray<AgentToolResult> results)
    {
        if (results.Length > _limits.MaximumToolResultsPerTurn)
        {
            throw new AgentLimitException();
        }

        long totalBytes = 0;
        foreach (var result in results)
        {
            if (result is null)
            {
                continue;
            }

            var byteCount = Encoding.UTF8.GetByteCount(result.Value.Content);
            totalBytes += byteCount;
            if (byteCount > _limits.MaximumToolResultBytes
                || totalBytes > _limits.MaximumTotalToolResultBytesPerTurn)
            {
                throw new AgentLimitException();
            }

            if (result.Value.Kind == AgentToolResultValueKind.Json)
            {
                ValidateToolResultJson(result.Value.Content);
            }
        }
    }

    private void ValidateToolResultJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _limits.MaximumJsonDepth,
                });
            var remainingNodes = _limits.MaximumJsonNodes;
            ValidateToolResultJsonNodes(document.RootElement, ref remainingNodes);
        }
        catch (JsonException)
        {
            throw new AgentLimitException();
        }
    }

    private static void ValidateToolResultJsonNodes(
        JsonElement element,
        ref int remainingNodes)
    {
        if (--remainingNodes < 0)
        {
            throw new AgentLimitException();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateToolResultJsonNodes(property.Value, ref remainingNodes);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateToolResultJsonNodes(item, ref remainingNodes);
            }
        }
    }

    private bool ToolResultsMatchPendingProposals(
        ImmutableArray<AgentToolResult> results)
    {
        if (results.Length != _pendingToolProposals.Length)
        {
            return false;
        }

        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            if (result is null || !ResultMatches(_pendingToolProposals[index], result))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ToolDefinitionsEqual(
        ImmutableArray<AgentToolDefinition> expected,
        ImmutableArray<AgentToolDefinition> actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var expectedTool = expected[index];
            var actualTool = actual[index];
            if (!string.Equals(expectedTool.Name, actualTool.Name, StringComparison.Ordinal)
                || !string.Equals(
                    expectedTool.Description,
                    actualTool.Description,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedTool.InputSchema.GetRawText(),
                    actualTool.InputSchema.GetRawText(),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
