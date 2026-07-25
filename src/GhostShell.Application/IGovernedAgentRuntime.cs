using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Presentation seam for one broker-governed agent run. Provider output remains
/// inert until this runtime prepares a closed action and the capability broker
/// authorizes it.
/// </summary>
public interface IGovernedAgentRuntime : IDisposable, IAsyncDisposable
{
    event EventHandler? Changed;

    GovernedAgentSnapshot Snapshot { get; }

    ValueTask<GovernedAgentSendResult> SendAsync(
        GovernedAgentPrompt request,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentSteeringResult> SteerAsync(
        GovernedAgentSteering request,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentDecisionResult> DecideAsync(
        AgentApprovalId approvalId,
        bool approved,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentQuestionResponseResult> RespondToQuestionAsync(
        AgentQuestionId questionId,
        GovernedAgentQuestionResponse response,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentCapabilityDecisionResult> DecideCapabilityRequestAsync(
        AgentCapabilityRequestId requestId,
        GovernedAgentCapabilityDecision decision,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentActionCancellationResult> CancelActiveActionAsync(
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentStopResult> StopAsync(
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentPolicyResult> EnableYoloAsync(
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentPolicyResult> DisableYoloAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Trusted local-human identity bound by the desktop composition root. Agent
/// prompts and model output never get to assert an approving client identity.
/// </summary>
public interface IAgentApprovalPrincipal
{
    ActorDescriptor Actor { get; }
}

public enum GovernedAgentState
{
    Ready,
    StreamingProvider,
    AwaitingUserInput,
    AwaitingCapabilityDecision,
    AwaitingApproval,
    RunningTool,
    Cancelling,
    Failed,
    Cancelled,
}

public sealed record GovernedAgentPrompt
{
    public const int MaximumMessageLength = 64 * 1024;

    public GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target)
        : this(providerId, message, target, policy: null, hasPolicyOverride: false)
    {
    }

    /// <summary>
    /// The policy is supplied by trusted desktop runtime provenance. Provider
    /// output and prompt text never participate in its selection.
    /// </summary>
    public GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        AgentPolicy policy)
        : this(providerId, message, target, policy, hasPolicyOverride: true)
    {
    }

    private GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        AgentPolicy? policy,
        bool hasPolicyOverride)
    {
        if (string.IsNullOrWhiteSpace(providerId.Value))
        {
            throw new ArgumentException(
                "An agent prompt requires an AI-provider profile.",
                nameof(providerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaximumMessageLength)
        {
            throw new ArgumentException(
                "The agent prompt exceeds its character limit.",
                nameof(message));
        }

        ArgumentNullException.ThrowIfNull(target);
        if (hasPolicyOverride
            && (policy is null || !policy.IsValidForDurableStorage()))
        {
            throw new ArgumentException(
                "A governed prompt requires a valid durable baseline policy.",
                nameof(policy));
        }

        if (hasPolicyOverride
            && !string.Equals(
                providerId.Value,
                policy!.Provider,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The durable agent policy provider must be the exact AI-provider profile identifier.",
                nameof(policy));
        }

        ProviderId = providerId;
        Message = string.Concat(message);
        Target = target;
        Policy = hasPolicyOverride
            ? AgentPolicyResolver.Resolve(policy!)
            : null;
    }

    public AiProviderProfileId ProviderId { get; }

    public string Message { get; }

    public AgentTarget Target { get; }

    /// <summary>
    /// A trusted run-specific override whose provider is the exact profile ID
    /// and whose model is passed unchanged to provider creation. Null preserves
    /// configured permissions while binding identity to the selected profile's
    /// captured default model.
    /// </summary>
    public AgentPolicy? Policy { get; }

}

public sealed record GovernedAgentApproval(
    AgentApprovalId Id,
    string ToolName,
    string ToolTitle,
    AgentActionRisk Risk,
    AgentPermission Permission,
    AgentTarget Target,
    AgentApprovalPresentation Presentation,
    DateTimeOffset ExpiresAtUtc,
    bool TemporarilyYieldsTerminalInput);

public sealed record GovernedAgentToolActivity(
    string ToolName,
    string ToolTitle,
    AgentActionRisk Risk,
    string TargetTitle,
    bool CancellationRequested = false);

/// <summary>
/// Presentation-safe evidence for one live, run-local YOLO authority window.
/// The capability broker remains authoritative; this value conveys no reusable
/// execution permission.
/// </summary>
public sealed record GovernedAgentYoloAuthority
{
    public GovernedAgentYoloAuthority(
        AgentRunId runId,
        AgentTarget target,
        DateTimeOffset confirmedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        AgentRunRegistration.ValidateRunId(runId);
        ArgumentNullException.ThrowIfNull(target);
        if (confirmedAtUtc.Offset != TimeSpan.Zero
            || expiresAtUtc.Offset != TimeSpan.Zero
            || expiresAtUtc <= confirmedAtUtc
            || expiresAtUtc - confirmedAtUtc > AgentYoloConfirmation.MaximumLifetime)
        {
            throw new ArgumentException(
                "A visible YOLO authority requires an ordered bounded UTC window.",
                nameof(expiresAtUtc));
        }

        RunId = runId;
        Target = target;
        ConfirmedAtUtc = confirmedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public AgentRunId RunId { get; }

    public AgentTarget Target { get; }

    public DateTimeOffset ConfirmedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record GovernedAgentSnapshot(
    GovernedAgentState State,
    AgentRunId? RunId,
    AiProviderProfileId? ProviderId,
    AgentTarget? Target,
    string TargetTitle,
    ImmutableArray<GovernedAgentContextItem> ContextItems,
    IReadOnlyList<AgentChatMessage> Messages,
    string ProvisionalAssistantText,
    string Status,
    GovernedAgentApproval? PendingApproval = null,
    GovernedAgentToolActivity? ActiveTool = null,
    bool TerminalMutationAvailable = false,
    string? CapabilityNotice = null,
    AgentPermission TerminalMutationPermission = AgentPermission.Ask,
    GovernedAgentYoloAuthority? YoloAuthority = null,
    string? ConnectionBoundary = null,
    string? WorkingDirectory = null,
    AgentPolicy? EffectivePolicy = null,
    GovernedAgentProgress? CurrentProgress = null,
    GovernedAgentQuestion? PendingQuestion = null,
    GovernedAgentCapabilityRequest? PendingCapabilityRequest = null,
    bool SteeringAvailable = false,
    long? SteeringGeneration = null)
{
    public bool IsBusy => State is
        GovernedAgentState.StreamingProvider
        or GovernedAgentState.AwaitingUserInput
        or GovernedAgentState.AwaitingCapabilityDecision
        or GovernedAgentState.AwaitingApproval
        or GovernedAgentState.RunningTool
        or GovernedAgentState.Cancelling;

    public bool CanSend =>
        State == GovernedAgentState.Ready
        && PendingApproval is null
        && PendingQuestion is null
        && PendingCapabilityRequest is null;

    public bool CanStop => RunId is not null
        && State != GovernedAgentState.Cancelled;

    public bool CanSteer =>
        SteeringAvailable
        && SteeringGeneration is > 0
        && State == GovernedAgentState.StreamingProvider
        && RunId is not null
        && PendingApproval is null
        && PendingQuestion is null
        && PendingCapabilityRequest is null
        && ActiveTool is null;

    public bool HasMessages =>
        Messages.Count > 0 || ProvisionalAssistantText.Length > 0;

    public bool HasYoloAuthority => YoloAuthority is not null;
}

public sealed record GovernedAgentSendResult(
    bool IsSuccess,
    string Code,
    string Message);

public sealed record GovernedAgentSteeringResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentDecisionResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentQuestionResponseResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentCapabilityDecisionResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentActionCancellationResult(
    bool WasRequested,
    string Code,
    string Message);

public sealed record GovernedAgentStopResult(
    bool WasRunning,
    string Code,
    string Message);

public sealed record GovernedAgentPolicyResult(
    bool IsAccepted,
    string Code,
    string Message);
