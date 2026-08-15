using System.Collections.Immutable;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    public async ValueTask RestoreLatestConversationAsync(
        CancellationToken cancellationToken)
    {
        if (_checkpointStore is null)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null
                || _restoredSession is not null
                || _snapshot.HasMessages
                || _turnCancellation is not null)
            {
                return;
            }
        }

        var listed = await ListCheckpointsAsync(
                MaximumConversationCatalogEntries,
                cancellationToken)
            .ConfigureAwait(false);
        if (!listed.IsSuccess || listed.Value is null)
        {
            return;
        }

        var catalog = await LoadConversationCatalogAsync(listed.Value, cancellationToken)
            .ConfigureAwait(false);
        var restored = catalog.FirstOrDefault()?.Session;

        if (restored is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed
                || _session is not null
                || _restoredSession is not null
                || _snapshot.HasMessages
                || _turnCancellation is not null)
            {
                return;
            }

            _restoredSession = restored;
            var descriptor = restored.DescribeConversation();
            var policy = descriptor.ProviderId is { } providerId
                && !string.IsNullOrWhiteSpace(descriptor.Model)
                ? new AgentPolicy(
                    providerId.Value,
                    descriptor.Model,
                    _configuredDefaultPolicy.Permissions)
                {
                    CompactionModel = _configuredDefaultPolicy.CompactionModel,
                    TitleModel = _configuredDefaultPolicy.TitleModel,
                    SystemPrompt = _configuredDefaultPolicy.SystemPrompt,
                }
                : _configuredDefaultPolicy;
            _baselinePolicy = policy;
            _runPolicy = policy;
            _effectivePolicy = policy;
            _snapshot = EmptySnapshot(policy) with
            {
                Messages = CopyMessages(ProjectMessages(restored)),
                ContextTokensUsed = restored.EstimateContextUsage().EstimatedTokens,
                ProviderId = descriptor.ProviderId,
                Model = descriptor.Model,
                EffectivePolicy = policy,
                Status = string.Empty,
                Conversations = catalog
                    .Select(item => item.Summary)
                    .ToImmutableArray(),
            };
        }

        NotifyChanged();
    }

    private async ValueTask<bool> PersistConversationAsync(
        NativeAgentSession session,
        CancellationToken cancellationToken)
    {
        if (_checkpointStore is null)
        {
            return true;
        }

        var captured = session.CaptureCheckpoint();
        if (!captured.Succeeded || captured.Checkpoint is null)
        {
            return false;
        }

        var saved = await SaveCheckpointAsync(captured.Checkpoint, cancellationToken)
            .ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return false;
        }

        await RefreshConversationCatalogAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> OpenConversationAsync(
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        if (_checkpointStore is null)
        {
            return false;
        }

        if (!await StartNewConversationAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var loaded = await LoadCheckpointAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return false;
        }

        var restored = NativeAgentSession.RestoreCheckpoint(loaded.Value);
        if (!restored.Succeeded || restored.Session is not { } session)
        {
            return false;
        }

        var descriptor = session.DescribeConversation();
        var policy = descriptor.ProviderId is { } providerId
            && !string.IsNullOrWhiteSpace(descriptor.Model)
            ? new AgentPolicy(
                providerId.Value,
                descriptor.Model,
                _configuredDefaultPolicy.Permissions)
            {
                CompactionModel = _configuredDefaultPolicy.CompactionModel,
                TitleModel = _configuredDefaultPolicy.TitleModel,
                SystemPrompt = _configuredDefaultPolicy.SystemPrompt,
            }
            : _configuredDefaultPolicy;
        lock (_gate)
        {
            if (_disposed || _turnCancellation is not null)
            {
                return false;
            }

            _restoredSession = session;
            _baselinePolicy = policy;
            _runPolicy = policy;
            _effectivePolicy = policy;
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.Ready,
                RunId = null,
                ProviderId = descriptor.ProviderId,
                Model = descriptor.Model,
                Messages = CopyMessages(ProjectMessages(session)),
                ContextTokensUsed = session.EstimateContextUsage().EstimatedTokens,
                EffectivePolicy = policy,
                Status = string.Empty,
            };
        }

        NotifyChanged();
        return true;
    }

    public async ValueTask<bool> ForkConversationAsync(
        AgentConversationForkPoint forkPoint,
        CancellationToken cancellationToken)
    {
        if (_checkpointStore is null)
        {
            return false;
        }

        NativeAgentSession source;
        AgentPolicy policy;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clearing
                || _policyChangeInFlight
                || _turnCancellation is not null
                || (_session ?? _restoredSession) is not { } available)
            {
                return false;
            }

            source = available;
            policy = _baselinePolicy;
        }

        var conversation = source.Snapshot().Conversation;
        if (forkPoint.MessageCount > conversation.Length)
        {
            return false;
        }

        var prefix = conversation[..forkPoint.MessageCount];
        if (prefix[^1] is not
            {
                Role: AgentMessageRole.Assistant,
                ToolCalls.Length: 0,
                ToolResult: null,
            })
        {
            return false;
        }

        NativeAgentSession fork;
        try
        {
            fork = new NativeAgentSession(AgentRunId.New(), prefix);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!await PersistConversationAsync(fork, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!await StartNewConversationAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await DeleteCheckpointAsync(fork.RunId, CancellationToken.None)
                .ConfigureAwait(false);
            await RefreshConversationCatalogAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        lock (_gate)
        {
            if (_disposed || _turnCancellation is not null)
            {
                return false;
            }

            _restoredSession = fork;
            _baselinePolicy = policy;
            _runPolicy = policy;
            _effectivePolicy = policy;
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.Ready,
                RunId = null,
                ProviderId = new AiProviderProfileId(policy.Provider),
                Model = policy.Model,
                Messages = CopyMessages(ProjectMessages(fork)),
                ContextTokensUsed = fork.EstimateContextUsage().EstimatedTokens,
                EffectivePolicy = policy,
                Status = string.Empty,
            };
        }

        NotifyChanged();
        return true;
    }

    public async ValueTask<bool> DeleteConversationAsync(
        AgentRunId runId,
        CancellationToken cancellationToken)
    {
        if (_checkpointStore is null)
        {
            return false;
        }

        AgentRunId? current;
        lock (_gate)
        {
            current = _session?.RunId ?? _restoredSession?.RunId;
        }

        if (current == runId
            && !await StartNewConversationAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var deleted = await DeleteCheckpointAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (!deleted.IsSuccess)
        {
            return false;
        }

        await RefreshConversationCatalogAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask RefreshConversationCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (_checkpointStore is null)
        {
            return;
        }

        var listed = await ListCheckpointsAsync(
                MaximumConversationCatalogEntries,
                cancellationToken)
            .ConfigureAwait(false);
        if (!listed.IsSuccess || listed.Value is null)
        {
            return;
        }

        var catalog = await LoadConversationCatalogAsync(listed.Value, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Conversations = catalog.Select(item => item.Summary).ToImmutableArray(),
            };
        }

        NotifyChanged();
    }

    private void PublishPendingConversation(
        AgentRunId runId,
        string userMessage,
        AiProviderProfileId providerId,
        string model)
    {
        var normalized = string.Join(
            ' ',
            userMessage.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        var title = normalized.Length switch
        {
            0 => "Image conversation",
            <= 72 => normalized,
            _ => string.Concat(normalized.AsSpan(0, 71), "…"),
        };
        var pending = new GovernedAgentConversationSummary(
            runId,
            title,
            providerId,
            model,
            MessageCount: 1,
            _timeProvider.GetUtcNow().ToUniversalTime());
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Conversations = (_snapshot.Conversations.IsDefault
                        ? ImmutableArray<GovernedAgentConversationSummary>.Empty
                        : _snapshot.Conversations)
                    .Where(item => item.RunId != runId)
                    .Prepend(pending)
                    .Take(MaximumConversationCatalogEntries)
                    .ToImmutableArray(),
            };
        }

        NotifyChanged();
    }

    private async ValueTask<IReadOnlyList<LoadedConversation>> LoadConversationCatalogAsync(
        IReadOnlyList<AgentSessionCheckpointSummary> stored,
        CancellationToken cancellationToken)
    {
        var conversations = new List<LoadedConversation>(stored.Count);
        foreach (var item in stored)
        {
            var loaded = await LoadCheckpointAsync(item.RunId, cancellationToken)
                .ConfigureAwait(false);
            if (!loaded.IsSuccess || loaded.Value is null)
            {
                continue;
            }

            var restored = NativeAgentSession.RestoreCheckpoint(loaded.Value);
            if (!restored.Succeeded || restored.Session is not { } session)
            {
                continue;
            }

            var descriptor = session.DescribeConversation();
            conversations.Add(new LoadedConversation(
                session,
                new GovernedAgentConversationSummary(
                    item.RunId,
                    descriptor.Title,
                    descriptor.ProviderId,
                    descriptor.Model,
                    descriptor.MessageCount,
                    item.UpdatedAt)));
        }

        return conversations;
    }

    private sealed record LoadedConversation(
        NativeAgentSession Session,
        GovernedAgentConversationSummary Summary);

    private ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveCheckpointAsync(
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        _conversationScopeId is { } scopeId
            ? _checkpointStore!.SaveAsync(scopeId, checkpoint, cancellationToken)
            : _checkpointStore!.SaveAsync(checkpoint, cancellationToken);

    private ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>>
        LoadCheckpointAsync(AgentRunId runId, CancellationToken cancellationToken) =>
        _conversationScopeId is { } scopeId
            ? _checkpointStore!.LoadAsync(scopeId, runId, cancellationToken)
            : _checkpointStore!.LoadAsync(runId, cancellationToken);

    private ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteCheckpointAsync(
        AgentRunId runId,
        CancellationToken cancellationToken) =>
        _conversationScopeId is { } scopeId
            ? _checkpointStore!.DeleteAsync(scopeId, runId, cancellationToken)
            : _checkpointStore!.DeleteAsync(runId, cancellationToken);

    private ValueTask<AgentSessionCheckpointStoreResult<
        IReadOnlyList<AgentSessionCheckpointSummary>>> ListCheckpointsAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
        _conversationScopeId is { } scopeId
            ? _checkpointStore!.ListAsync(scopeId, maximumCount, cancellationToken)
            : _checkpointStore!.ListAsync(maximumCount, cancellationToken);

    private void ReportCheckpointSaveFailure()
    {
        lock (_gate)
        {
            if (_disposed || _snapshot.State != GovernedAgentState.Ready)
            {
                return;
            }

            _snapshot = _snapshot with
            {
                Status = "This conversation could not be saved locally.",
            };
        }

        NotifyChanged();
    }
}
