using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.Core;

namespace GhostShell.Agent;

public sealed partial class NativeAgentSession
{
    private const string CheckpointReadyState = "ready";
    private const int MaximumRouteIdentityLength = 256;

    private AiProviderProfileId? _conversationProviderId;
    private string? _conversationModel;

    private static readonly JsonSerializerOptions CheckpointJsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 160,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> SecretValuePropertyNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "passphrase",
        "privateKey",
        "apiKey",
        "authorization",
        "credential",
        "accessToken",
        "refreshToken",
        "secret",
        "secretRef",
        "secretReference",
        "secretValue",
        "credentialValue",
        "token",
    };

    public AgentConversationDescriptor DescribeConversation()
    {
        lock (_gate)
        {
            var firstUserMessage = _conversation.FirstOrDefault(message =>
                message.Role == AgentMessageRole.User
                && !string.IsNullOrWhiteSpace(message.Content));
            var title = _conversationTitle ?? (firstUserMessage is null
                ? "Untitled conversation"
                : CreateConversationTitle(firstUserMessage.Content));
            var binding = _conversation
                .Select(message => message.ProviderReplayState?.Binding)
                .LastOrDefault(candidate => candidate is not null);
            return new AgentConversationDescriptor(
                RunId,
                title,
                binding?.ProfileId ?? _conversationProviderId,
                binding?.Model ?? _conversationModel,
                _conversation.Count(message => message.Role is
                    AgentMessageRole.User or AgentMessageRole.Assistant));
        }
    }

    public bool TrySetConversationRoute(
        AiProviderProfileId providerId,
        string model)
    {
        if (providerId == default)
        {
            throw new ArgumentException(
                "The conversation provider profile is required.",
                nameof(providerId));
        }

        if (!IsValidRouteIdentity(model))
        {
            throw new ArgumentException(
                "The conversation model is invalid.",
                nameof(model));
        }

        lock (_gate)
        {
            if (_state != NativeAgentSessionState.Ready
                || _activeTurn is not null
                || _providerOperationsInFlight != 0
                || _pendingToolTurn is not null
                || _pendingToolProposals.Length != 0
                || _activeCompaction is not null)
            {
                return false;
            }

            _conversationProviderId = providerId;
            _conversationModel = model.Trim();
            return true;
        }
    }

    private static string CreateConversationTitle(string content)
    {
        const int maximumLength = 72;
        var normalized = string.Join(
            ' ',
            content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumLength - 1), "…");
    }

    /// <summary>
    /// Captures only a fully committed idle transcript. In-flight streams,
    /// pending tool decisions, compaction leases, provider clients, and any
    /// execution authority are process-local and cannot enter this document.
    /// </summary>
    public AgentCheckpointCaptureResult CaptureCheckpoint()
    {
        lock (_gate)
        {
            if (_state != NativeAgentSessionState.Ready
                || _activeTurn is not null
                || _providerOperationsInFlight != 0
                || _pendingToolTurn is not null
                || _pendingToolProposals.Length != 0
                || _activeCompaction is not null)
            {
                return AgentCheckpointCaptureResult.Failure(
                    AgentCheckpointCaptureErrorCode.SessionNotIdle);
            }

            try
            {
                ValidateConversation(_conversation);
                var durableConversation = _conversation
                    .Select(WithoutUnsafeProviderReplayState)
                    .ToImmutableArray();
                ValidateConversation(durableConversation);
                ValidateCheckpointDurableBounds(durableConversation);
                var providerBinding = _conversation
                    .Select(message => message.ProviderReplayState?.Binding)
                    .LastOrDefault(candidate => candidate is not null);

                if (ContainsUnsafeStructuredContent(durableConversation))
                {
                    return AgentCheckpointCaptureResult.Failure(
                        AgentCheckpointCaptureErrorCode.UnsafeContent);
                }

                var payload = new CheckpointPayload(
                    CheckpointReadyState,
                    _conversationTitle,
                    _conversationRevision,
                    _sequence,
                    _lastSubmittedToolGeneration,
                    durableConversation.Select(ToCheckpointMessage).ToArray(),
                    _providerToolBindings
                        .OrderBy(binding => binding.Key, StringComparer.Ordinal)
                        .Select(binding => new CheckpointToolBinding(
                            binding.Key,
                            binding.Value))
                        .ToArray(),
                    providerBinding?.ProfileId.Value ?? _conversationProviderId?.Value,
                    providerBinding?.Model ?? _conversationModel);
                var payloadJson = JsonSerializer.Serialize(
                    payload,
                    CheckpointJsonOptions);
                var checkpoint = new AgentSessionCheckpoint(
                    RunId,
                    AgentSessionCheckpoint.CurrentSchemaVersion,
                    _generation,
                    _revision,
                    payloadJson,
                    _timeProvider.GetUtcNow().ToUniversalTime());
                return AgentCheckpointCaptureResult.Success(checkpoint);
            }
            catch (Exception exception) when (
                exception is AgentLimitException
                    or AgentConversationException
                    or ArgumentException
                    or JsonException
                    or NotSupportedException
                    or OverflowException)
            {
                return AgentCheckpointCaptureResult.Failure(
                    AgentCheckpointCaptureErrorCode.LimitExceeded);
            }
        }
    }

    /// <summary>
    /// Rehydrates a new idle session from a kernel-owned checkpoint document.
    /// No runtime capability or provider object is accepted or reconstructed.
    /// </summary>
    public static AgentCheckpointRestoreResult RestoreCheckpoint(
        AgentSessionCheckpoint checkpoint,
        AgentKernelLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.SchemaVersion is not (1 or AgentSessionCheckpoint.CurrentSchemaVersion))
        {
            return AgentCheckpointRestoreResult.Failure(
                AgentCheckpointRestoreErrorCode.UnsupportedSchema);
        }

        CheckpointPayload? payload;
        try
        {
            using (var document = JsonDocument.Parse(
                checkpoint.PayloadJson,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 160,
                }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return AgentCheckpointRestoreResult.Failure(
                        AgentCheckpointRestoreErrorCode.InvalidPayload);
                }
            }

            payload = JsonSerializer.Deserialize<CheckpointPayload>(
                checkpoint.PayloadJson,
                CheckpointJsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            return AgentCheckpointRestoreResult.Failure(
                AgentCheckpointRestoreErrorCode.InvalidPayload);
        }

        if (!TryValidateEnvelope(checkpoint, payload))
        {
            return AgentCheckpointRestoreResult.Failure(
                AgentCheckpointRestoreErrorCode.InvalidPayload);
        }

        try
        {
            var conversation = payload!.Conversation!
                .Select(FromCheckpointMessage)
                .ToImmutableArray();
            if (ContainsUnsafeStructuredContent(conversation))
            {
                return AgentCheckpointRestoreResult.Failure(
                    AgentCheckpointRestoreErrorCode.UnsafeContent);
            }

            var session = new NativeAgentSession(
                checkpoint.RunId,
                conversation,
                limits,
                TimeProvider.System);
            session.ValidateCheckpointDurableBounds(conversation);
            var bindings = payload.ProviderToolBindings!
                .Select(binding => new KeyValuePair<string, string>(
                    binding.ProviderName,
                    binding.ToolName))
                .ToArray();
            session.RegisterProviderToolBindings(bindings);
            if (session._providerToolBindings.Count != bindings.Length
                || bindings.Any(binding =>
                    !session._providerToolBindings.TryGetValue(
                        binding.Key,
                        out var toolName)
                    || !string.Equals(
                        binding.Value,
                        toolName,
                        StringComparison.Ordinal)))
            {
                return AgentCheckpointRestoreResult.Failure(
                    AgentCheckpointRestoreErrorCode.InvalidPayload);
            }

            var greatestTranscriptGeneration = conversation
                .SelectMany(message => message.ToolCalls)
                .Select(proposal => proposal.Generation)
                .Concat(conversation
                    .Where(message => message.ToolResult is not null)
                    .Select(message => message.ToolResult!.Generation))
                .DefaultIfEmpty(0)
                .Max();
            if (greatestTranscriptGeneration > checkpoint.Generation)
            {
                return AgentCheckpointRestoreResult.Failure(
                    AgentCheckpointRestoreErrorCode.InvalidPayload);
            }

            session._conversationRevision = payload.ConversationRevision;
            session._conversationTitle = payload.Title is null
                ? null
                : NormalizeConversationTitle(payload.Title);
            session._conversationProviderId = payload.ProviderId is null
                ? null
                : new AiProviderProfileId(payload.ProviderId);
            session._conversationModel = payload.Model;
            session._generation = checkpoint.Generation;
            session._revision = checkpoint.Revision;
            session._sequence = payload.LastSequence;
            session._lastSubmittedToolGeneration = payload.LastSubmittedToolGeneration;
            return AgentCheckpointRestoreResult.Success(session);
        }
        catch (AgentLimitException)
        {
            return AgentCheckpointRestoreResult.Failure(
                AgentCheckpointRestoreErrorCode.LimitExceeded);
        }
        catch (ArgumentException exception)
            when (exception.InnerException is AgentLimitException)
        {
            return AgentCheckpointRestoreResult.Failure(
                AgentCheckpointRestoreErrorCode.LimitExceeded);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or AgentConversationException
                or InvalidOperationException
                or JsonException
                or OverflowException)
        {
            return AgentCheckpointRestoreResult.Failure(
                AgentCheckpointRestoreErrorCode.InvalidPayload);
        }
    }

    private static bool TryValidateEnvelope(
        AgentSessionCheckpoint checkpoint,
        CheckpointPayload? payload)
    {
        if (payload is null
            || !string.Equals(
                payload.State,
                CheckpointReadyState,
                StringComparison.Ordinal)
            || payload.Conversation is null
            || payload.ProviderToolBindings is null
            || payload.ConversationRevision < 0
            || payload.ConversationRevision > checkpoint.Revision
            || payload.LastSequence < 0
            || payload.LastSequence != checkpoint.Revision
            || payload.LastSubmittedToolGeneration < 0
            || payload.LastSubmittedToolGeneration > checkpoint.Generation
            || payload.ProviderId is null != (payload.Model is null)
            || payload.ProviderId is not null
                && (!IsValidRouteIdentity(payload.ProviderId)
                    || !IsValidRouteIdentity(payload.Model))
            || checkpoint.Generation > checkpoint.Revision)
        {
            return false;
        }

        var providerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in payload.ProviderToolBindings)
        {
            if (binding is null
                || !providerNames.Add(binding.ProviderName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidRouteIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumRouteIdentityLength
        && !value.Any(char.IsControl);

    private static CheckpointMessage ToCheckpointMessage(AgentMessage message) =>
        new(
            ToRoleToken(message.Role),
            message.Content,
            message.ToolCalls.Select(toolCall => new CheckpointToolCall(
                toolCall.Id,
                toolCall.Generation,
                toolCall.ProviderCallId,
                toolCall.ProviderName,
                toolCall.ToolName,
                toolCall.Arguments.Clone())).ToArray(),
            message.ToolResult is null
                ? null
                : ToCheckpointToolResult(message.ToolResult),
            message.ReasoningSummary,
            message.Usage is null
                ? null
                : new CheckpointTokenUsage(
                    message.Usage.InputTokens,
                    message.Usage.OutputTokens,
                    message.Usage.CachedInputTokens,
                    message.Usage.ReasoningTokens),
            message.Images
                .Select(image => new CheckpointImage(
                    image.FileName,
                    image.MediaType,
                    Convert.ToBase64String(image.Content)))
                .ToArray(),
            message.ProviderReplayState is null
                ? null
                : ToCheckpointReplayState(message.ProviderReplayState),
            message.RequestedReasoningEffort is { } effort
                ? ToReasoningEffortToken(effort)
                : null);

    private static AgentMessage WithoutUnsafeProviderReplayState(
        AgentMessage message) =>
        message.ProviderReplayState?.ContainsSuppressedRawReasoning == true
            ? AgentMessage.Assistant(
                message.Content,
                message.ToolCalls,
                message.ReasoningSummary,
                message.Usage,
                providerReplayState: null,
                requestedReasoningEffort: message.RequestedReasoningEffort)
            : message;

    private static CheckpointProviderReplayState ToCheckpointReplayState(
        AgentProviderReplayState state) =>
        new(
            state.Binding.ProfileId.Value,
            (int)state.Binding.ProviderKind,
            (int)state.Binding.Protocol,
            state.Binding.Model,
            state.Binding.Endpoint.AbsoluteUri,
            state.Binding.RouteIdentity,
            (int)state.Format,
            state.ContainsSuppressedRawReasoning,
            state.Items.Select(item => new CheckpointProviderReplayItem(
                item.Index,
                (int)item.Kind,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(item.PayloadJson)),
                item.ToolIndex)).ToArray());

    private static CheckpointToolResult ToCheckpointToolResult(AgentToolResult result)
    {
        string? textValue = null;
        JsonElement? jsonValue = null;
        if (result.Value.Kind == AgentToolResultValueKind.Text)
        {
            textValue = result.Value.Content;
        }
        else
        {
            using var document = JsonDocument.Parse(result.Value.Content);
            jsonValue = document.RootElement.Clone();
        }

        return new CheckpointToolResult(
            result.ProposalId,
            result.Generation,
            result.ProviderCallId,
            ToStatusToken(result.Status),
            result.StableCode,
            ToValueKindToken(result.Value.Kind),
            textValue,
            jsonValue);
    }

    private static AgentMessage FromCheckpointMessage(CheckpointMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Content);
        ArgumentNullException.ThrowIfNull(message.ToolCalls);
        var role = FromRoleToken(message.Role);
        var images = (message.Images ?? [])
            .Select(FromCheckpointImage)
            .ToImmutableArray();
        var toolCalls = message.ToolCalls
            .Select(FromCheckpointToolCall)
            .ToImmutableArray();
        var usage = message.Usage is null
            ? null
            : new AgentTokenUsage(
                message.Usage.InputTokens,
                message.Usage.OutputTokens,
                message.Usage.CachedInputTokens,
                message.Usage.ReasoningTokens);

        if (role == AgentMessageRole.Assistant)
        {
            if (message.ToolResult is not null || images.Length != 0)
            {
                throw new ArgumentException("An assistant checkpoint message is invalid.");
            }

            return AgentMessage.Assistant(
                message.Content,
                toolCalls,
                message.ReasoningSummary,
                usage,
                message.ProviderReplayState is null
                    ? null
                    : FromCheckpointReplayState(message.ProviderReplayState),
                message.RequestedReasoningEffort is null
                    ? null
                    : FromReasoningEffortToken(message.RequestedReasoningEffort));
        }

        if (role == AgentMessageRole.Tool)
        {
            if (toolCalls.Length != 0
                || message.ToolResult is null
                || message.ReasoningSummary is not null
                || usage is not null
                || images.Length != 0
                || message.ProviderReplayState is not null
                || message.RequestedReasoningEffort is not null)
            {
                throw new ArgumentException("A tool checkpoint message is invalid.");
            }

            var result = FromCheckpointToolResult(message.ToolResult);
            if (!string.Equals(message.Content, result.Value.Content, StringComparison.Ordinal))
            {
                throw new ArgumentException("Tool checkpoint content does not match its result.");
            }

            return AgentMessage.FromToolResult(result);
        }

        if (toolCalls.Length != 0
            || message.ToolResult is not null
            || message.ReasoningSummary is not null
            || usage is not null
            || message.ProviderReplayState is not null
            || message.RequestedReasoningEffort is not null
            || (role != AgentMessageRole.User && images.Length != 0))
        {
            throw new ArgumentException("A plain checkpoint message is invalid.");
        }

        return images.Length == 0
            ? new AgentMessage(role, message.Content)
            : new AgentMessage(role, message.Content, images);
    }

    private static AgentProviderReplayState FromCheckpointReplayState(
        CheckpointProviderReplayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.ProfileId);
        ArgumentNullException.ThrowIfNull(state.Model);
        ArgumentNullException.ThrowIfNull(state.Endpoint);
        ArgumentNullException.ThrowIfNull(state.RouteIdentity);
        ArgumentNullException.ThrowIfNull(state.Items);
        if (!Enum.IsDefined((AiProviderKind)state.ProviderKind)
            || !Enum.IsDefined((AiProviderProtocol)state.Protocol)
            || !Enum.IsDefined((AgentProviderReplayFormat)state.Format)
            || state.ContainsSuppressedRawReasoning
            || state.Items.Length is 0 or > AgentProviderReplayState.MaximumItems)
        {
            throw new ArgumentException("A provider replay checkpoint is invalid.");
        }

        var items = state.Items
            .Select(FromCheckpointReplayItem)
            .ToImmutableArray();
        var replayState = new AgentProviderReplayState(
            new AgentProviderReplayBinding(
                new AiProviderProfileId(state.ProfileId),
                (AiProviderKind)state.ProviderKind,
                (AiProviderProtocol)state.Protocol,
                state.Model,
                new Uri(state.Endpoint, UriKind.Absolute),
                state.RouteIdentity),
            (AgentProviderReplayFormat)state.Format,
            items);
        if (replayState.ContainsSuppressedRawReasoning)
        {
            throw new ArgumentException("A provider replay checkpoint is invalid.");
        }

        return replayState;
    }

    private static AgentProviderReplayItem FromCheckpointReplayItem(
        CheckpointProviderReplayItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.PayloadBase64);
        if (!Enum.IsDefined((AgentProviderReplayItemKind)item.Kind)
            || item.PayloadBase64.Length is 0
                or > ((AgentProviderReplayState.MaximumItemBytes + 2) / 3 * 4))
        {
            throw new ArgumentException("A provider replay item is invalid.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(item.PayloadBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A provider replay item is invalid.",
                exception);
        }

        string payloadJson;
        try
        {
            payloadJson = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                "A provider replay item is invalid.",
                exception);
        }

        return new AgentProviderReplayItem(
            item.Index,
            (AgentProviderReplayItemKind)item.Kind,
            payloadJson,
            item.ToolIndex);
    }

    private static AgentImageAttachment FromCheckpointImage(CheckpointImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(image.ContentBase64);
        var maximumBase64Length = checked(
            (AgentImageAttachment.MaximumBytes + 2) / 3 * 4);
        if (image.ContentBase64.Length is 0
            || image.ContentBase64.Length > maximumBase64Length)
        {
            throw new ArgumentException("An image checkpoint value is invalid.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(image.ContentBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "An image checkpoint value is invalid.",
                exception);
        }

        return new AgentImageAttachment(image.FileName, image.MediaType, bytes);
    }

    private static AgentToolProposal FromCheckpointToolCall(CheckpointToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var proposal = new AgentToolProposal(
            toolCall.Id,
            toolCall.Generation,
            toolCall.ProviderCallId,
            toolCall.ToolName,
            toolCall.Arguments);
        if (!string.Equals(
                proposal.ProviderName,
                toolCall.ProviderName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("A provider tool binding is invalid.");
        }

        return proposal;
    }

    private static AgentToolResult FromCheckpointToolResult(
        CheckpointToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var kind = FromValueKindToken(result.ValueKind);
        AgentToolResultValue value;
        if (kind == AgentToolResultValueKind.Text)
        {
            if (result.TextValue is null || result.JsonValue is not null)
            {
                throw new ArgumentException("A text tool-result checkpoint is invalid.");
            }

            value = AgentToolResultValue.FromText(result.TextValue);
        }
        else
        {
            if (result.TextValue is not null
                || result.JsonValue is not { } jsonValue
                || jsonValue.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException("A JSON tool-result checkpoint is invalid.");
            }

            value = AgentToolResultValue.FromJson(
                Encoding.UTF8.GetBytes(jsonValue.GetRawText()));
        }

        return new AgentToolResult(
            result.ProposalId,
            result.Generation,
            result.ProviderCallId,
            FromStatusToken(result.Status),
            result.StableCode,
            value);
    }

    private static bool ContainsUnsafeStructuredContent(
        ImmutableArray<AgentMessage> conversation)
    {
        foreach (var message in conversation)
        {
            if (LiteralSecretValidator.ContainsLikelyLiteralSecret(message.Content)
                || (message.ReasoningSummary is { } reasoningSummary
                    && LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        reasoningSummary)))
            {
                return true;
            }

            if (message.Images.Any(image =>
                    LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        image.FileName)))
            {
                return true;
            }

            foreach (var proposal in message.ToolCalls)
            {
                if (LiteralSecretValidator.ContainsLikelyLiteralSecret(proposal.Id)
                    || LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        proposal.ProviderCallId)
                    || LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        proposal.ToolName)
                    || LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        proposal.Arguments.GetRawText())
                    || ContainsReservedSecretProperty(proposal.Arguments))
                {
                    return true;
                }
            }

            if (message.ToolResult is { } result)
            {
                if (LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        result.ProposalId)
                    || LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        result.ProviderCallId)
                    || LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        result.StableCode)
                    || LiteralSecretValidator.ContainsLikelyLiteralSecret(
                        result.Value.Content))
                {
                    return true;
                }

                if (result.Value.Kind == AgentToolResultValueKind.Json)
                {
                    using var document = JsonDocument.Parse(result.Value.Content);
                    if (ContainsReservedSecretProperty(document.RootElement))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void ValidateCheckpointDurableBounds(
        ImmutableArray<AgentMessage> conversation)
    {
        for (var index = 0; index < conversation.Length; index++)
        {
            var message = conversation[index];
            ValidateMessageBounds(message, _limits.MaximumAssistantTextBytes);
            if (message.ToolCalls.Length == 0)
            {
                continue;
            }

            long totalArgumentBytes = 0;
            foreach (var proposal in message.ToolCalls)
            {
                AgentToolDefinition.ValidateIdentifier(
                    proposal.Id,
                    nameof(conversation),
                    512);
                AgentToolDefinition.ValidateIdentifier(
                    proposal.ProviderCallId,
                    nameof(conversation),
                    256);
                AgentToolDefinition.ValidateIdentifier(
                    proposal.ToolName,
                    nameof(conversation),
                    AgentToolDefinition.MaximumNameLength);
                AgentToolDefinition.ValidateProviderName(
                    proposal.ProviderName,
                    nameof(conversation));
                var argumentBytes = Encoding.UTF8.GetByteCount(
                    proposal.Arguments.GetRawText());
                totalArgumentBytes = checked(totalArgumentBytes + argumentBytes);
                if (argumentBytes > _limits.MaximumToolArgumentBytes
                    || totalArgumentBytes
                        > _limits.MaximumTotalToolArgumentBytesPerTurn)
                {
                    throw new AgentLimitException();
                }

                var remainingNodes = _limits.MaximumJsonNodes;
                ValidateToolSchemaWithinLimits(
                    proposal.Arguments,
                    1,
                    ref remainingNodes);
            }

            var resultBuilder = ImmutableArray.CreateBuilder<AgentToolResult>(
                message.ToolCalls.Length);
            for (var resultOffset = 1;
                 resultOffset <= message.ToolCalls.Length;
                 resultOffset++)
            {
                if (index + resultOffset >= conversation.Length
                    || conversation[index + resultOffset].ToolResult is not { } result)
                {
                    throw new AgentConversationException();
                }

                resultBuilder.Add(result);
            }

            ValidateToolResultBounds(resultBuilder.MoveToImmutable());
        }
    }

    private static bool ContainsReservedSecretProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (SecretValuePropertyNames.Contains(property.Name)
                    || ContainsReservedSecretProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsReservedSecretProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ToRoleToken(AgentMessageRole role) => role switch
    {
        AgentMessageRole.System => "system",
        AgentMessageRole.User => "user",
        AgentMessageRole.Assistant => "assistant",
        AgentMessageRole.Tool => "tool",
        AgentMessageRole.Summary => "summary",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static AgentMessageRole FromRoleToken(string token) => token switch
    {
        "system" => AgentMessageRole.System,
        "user" => AgentMessageRole.User,
        "assistant" => AgentMessageRole.Assistant,
        "tool" => AgentMessageRole.Tool,
        "summary" => AgentMessageRole.Summary,
        _ => throw new ArgumentException("The checkpoint message role is invalid."),
    };

    private static string ToReasoningEffortToken(AgentReasoningEffort effort) => effort switch
    {
        AgentReasoningEffort.Automatic => "automatic",
        AgentReasoningEffort.Off => "off",
        AgentReasoningEffort.Minimal => "minimal",
        AgentReasoningEffort.Low => "low",
        AgentReasoningEffort.Medium => "medium",
        AgentReasoningEffort.High => "high",
        AgentReasoningEffort.ExtraHigh => "extra_high",
        AgentReasoningEffort.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(effort)),
    };

    private static AgentReasoningEffort FromReasoningEffortToken(string token) => token switch
    {
        "automatic" => AgentReasoningEffort.Automatic,
        "off" => AgentReasoningEffort.Off,
        "minimal" => AgentReasoningEffort.Minimal,
        "low" => AgentReasoningEffort.Low,
        "medium" => AgentReasoningEffort.Medium,
        "high" => AgentReasoningEffort.High,
        "extra_high" => AgentReasoningEffort.ExtraHigh,
        "max" => AgentReasoningEffort.Max,
        _ => throw new ArgumentException(
            "The checkpoint reasoning effort is invalid."),
    };

    private static string ToStatusToken(AgentToolResultStatus status) => status switch
    {
        AgentToolResultStatus.Succeeded => "succeeded",
        AgentToolResultStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static AgentToolResultStatus FromStatusToken(string token) => token switch
    {
        "succeeded" => AgentToolResultStatus.Succeeded,
        "failed" => AgentToolResultStatus.Failed,
        _ => throw new ArgumentException("The checkpoint tool-result status is invalid."),
    };

    private static string ToValueKindToken(AgentToolResultValueKind kind) => kind switch
    {
        AgentToolResultValueKind.Text => "text",
        AgentToolResultValueKind.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static AgentToolResultValueKind FromValueKindToken(string token) => token switch
    {
        "text" => AgentToolResultValueKind.Text,
        "json" => AgentToolResultValueKind.Json,
        _ => throw new ArgumentException("The checkpoint tool-result kind is invalid."),
    };

    private sealed record CheckpointPayload(
        string State,
        string? Title,
        long ConversationRevision,
        long LastSequence,
        long LastSubmittedToolGeneration,
        CheckpointMessage[]? Conversation,
        CheckpointToolBinding[]? ProviderToolBindings,
        string? ProviderId = null,
        string? Model = null);

    private sealed record CheckpointMessage(
        string Role,
        string Content,
        CheckpointToolCall[]? ToolCalls,
        CheckpointToolResult? ToolResult,
        string? ReasoningSummary,
        CheckpointTokenUsage? Usage,
        CheckpointImage[]? Images,
        CheckpointProviderReplayState? ProviderReplayState,
        string? RequestedReasoningEffort);

    private sealed record CheckpointProviderReplayState(
        string ProfileId,
        int ProviderKind,
        int Protocol,
        string Model,
        string Endpoint,
        string RouteIdentity,
        int Format,
        bool ContainsSuppressedRawReasoning,
        CheckpointProviderReplayItem[]? Items);

    private sealed record CheckpointProviderReplayItem(
        int Index,
        int Kind,
        string PayloadBase64,
        int? ToolIndex);

    private sealed record CheckpointToolCall(
        string Id,
        long Generation,
        string ProviderCallId,
        string ProviderName,
        string ToolName,
        JsonElement Arguments);

    private sealed record CheckpointToolResult(
        string ProposalId,
        long Generation,
        string ProviderCallId,
        string Status,
        string StableCode,
        string ValueKind,
        string? TextValue,
        JsonElement? JsonValue);

    private sealed record CheckpointTokenUsage(
        long InputTokens,
        long OutputTokens,
        long CachedInputTokens,
        long ReasoningTokens);

    private sealed record CheckpointImage(
        string FileName,
        string MediaType,
        string ContentBase64);

    private sealed record CheckpointToolBinding(
        string ProviderName,
        string ToolName);
}
