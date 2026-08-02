using System.Collections.ObjectModel;
using System.Globalization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record AgentChatMessageViewModel(
    AgentChatMessageRole Role,
    string Content)
{
    public bool IsUser => Role == AgentChatMessageRole.User;

    public bool IsAssistant => Role == AgentChatMessageRole.Assistant;

    public string Author => IsUser ? "You" : "GhostSHELL";
}

public sealed record AgentApprovalArgumentViewModel(
    string Name,
    string DisplayValue,
    bool IsSensitive);

public sealed record AgentApprovalCardViewModel(
    AgentApprovalId Id,
    string ToolName,
    string ToolTitle,
    string Risk,
    string Permission,
    string TargetTitle,
    string ExactTarget,
    string Host,
    string WorkingDirectory,
    IReadOnlyList<AgentApprovalArgumentViewModel> Arguments,
    string ExpiresAt,
    bool TemporarilyYieldsTerminalInput)
{
    public bool HasArguments => Arguments.Count > 0;

    public string InputYieldWarning =>
        "Approving temporarily yields terminal input to the agent for this one action. "
        + "Your next physical input preempts it.";
}

public sealed record AgentToolActivityViewModel(
    string ToolName,
    string ToolTitle,
    string Risk,
    string TargetTitle,
    bool CancellationRequested);

/// <summary>
/// Immutable presentation copy of one bounded, run-local progress report.
/// The message remains untrusted display text and is never promoted to chat
/// history or durable audit evidence.
/// </summary>
public sealed record AgentProgressViewModel
{
    public AgentProgressViewModel(GovernedAgentProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Message = string.Concat(progress.Message);
        Percent = progress.Percent;
        ContentOrigin = progress.ContentOrigin;
    }

    public string Message { get; }

    public int? Percent { get; }

    public string ContentOrigin { get; }

    public bool HasPercent => Percent.HasValue;

    public bool IsIndeterminate => !HasPercent;

    public double ProgressValue => Percent.GetValueOrDefault();

    public string PercentLabel => Percent is { } percent
        ? $"{percent.ToString(CultureInfo.InvariantCulture)}%"
        : string.Empty;

    public string AccessibleName => Percent is { } percent
        ? $"AI agent progress · {Message} · "
            + $"{percent.ToString(CultureInfo.InvariantCulture)} percent"
        : $"AI agent progress · {Message} · in progress";
}

/// <summary>
/// Presentation-only copy of a bounded model clarification. The question text
/// remains visibly untrusted and never participates in approval or authority.
/// </summary>
public sealed record AgentQuestionCardViewModel
{
    public AgentQuestionCardViewModel(GovernedAgentQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        Id = question.Id;
        Question = string.Concat(question.Question);
        ExpiresAt = AgentPresentationTime.Friendly(question.ExpiresAtUtc);
        ContentOrigin = question.ContentOrigin;
    }

    public AgentQuestionId Id { get; }

    public string Question { get; }

    public string ExpiresAt { get; }

    public string ContentOrigin { get; }

    public string ResponseWarning =>
        "Your answer is clarification only—not approval. Do not include passwords, "
        + "tokens, private keys, or other credentials.";

    public string AccessibleName =>
        $"AI agent question · untrusted model text · {Question} · expires {ExpiresAt}";
}

/// <summary>
/// Presentation-only copy of one authenticated request to change a single
/// run-local capability from Off to Ask. Every visible value originates from
/// trusted runtime metadata; model prose is deliberately absent.
/// </summary>
public sealed record AgentCapabilityRequestCardViewModel(
    AgentCapabilityRequestId Id,
    string DisplayTitle,
    string CapabilityToken,
    string TargetTitle,
    string ExactTarget,
    IReadOnlyList<string> AffectedToolTitles,
    string ExpiresAt)
{
    public string GrantWarning =>
        "Enabling Ask grants no action. Every later terminal, file, browser, "
        + "or process operation still needs its ordinary exact approval.";

    public string AccessibleName =>
        $"AI agent capability request · {DisplayTitle} · Off to Ask for this run · "
        + $"target {TargetTitle} · expires {ExpiresAt}";
}

public sealed record AgentAuditEntryViewModel(
    string Kind,
    string Title,
    string ToolName,
    string Outcome,
    string Evidence,
    string Timeline,
    string Result,
    string TargetBinding,
    string OccurredAt,
    string AccessibleName)
{
    public bool HasToolName => ToolName.Length > 0;

    public bool HasResult => Result.Length > 0;
}

public sealed record AgentContextItemViewModel(
    PanelKind Kind,
    string Title,
    string TabTitle,
    string ExactIdentity,
    string Context,
    string State,
    string Operations,
    string AccessibleName);

public sealed record AgentPolicyCapabilityViewModel(
    string Capability,
    string Permission)
{
    public string AccessibleName => $"{Capability} · {Permission}";
}

public sealed record AgentYoloAuthorityViewModel(
    string Scope,
    string Duration,
    string ExpiresAt)
{
    public string Warning =>
        "Terminal input and destructive terminal actions can run without "
        + "per-action approval. Exact scope, human preemption, stop, and audit remain active.";
}

public sealed class AgentChatViewModel : ObservableObject, IDisposable
{
    private const string PendingCapabilityNotice =
        "Capabilities are verified against the selected panel scope when you send.";
    private const string ExactPanelYoloRequirement =
        "Run-only YOLO requires an exact terminal panel. "
        + "Clear this run and start a panel-scoped run to enable it.";

    private readonly IGovernedAgentRuntime _runtime;
    private readonly IAiProviderProfileRuntime _profiles;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly IAgentRunAuditReader? _auditReader;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sendGate = new();
    private Task _activeSend = Task.CompletedTask;
    private AiProviderProfileDescriptor? _selectedProvider;
    private string _prompt = string.Empty;
    private string _status = string.Empty;
    private string _provisionalAssistantText = string.Empty;
    private string _questionResponseDraft = string.Empty;
    private string _targetTitle = "No panel selected";
    private string _exactTarget = string.Empty;
    private string _connectionBoundary = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _capabilityNotice = PendingCapabilityNotice;
    private string _effectivePolicyProvider = AgentPolicy.Default.Provider;
    private string _effectivePolicyModel = AgentPolicy.Default.Model;
    private GovernedAgentState _state;
    private AgentApprovalCardViewModel? _pendingApproval;
    private AgentQuestionCardViewModel? _pendingQuestion;
    private AgentCapabilityRequestCardViewModel? _pendingCapabilityRequest;
    private AgentToolActivityViewModel? _activeTool;
    private AgentProgressViewModel? _currentProgress;
    private AgentYoloAuthorityViewModel? _yoloAuthority;
    private AgentRunId? _runId;
    private AgentRunId? _auditRunId;
    private AgentRunAuditCursor? _nextAuditCursor;
    private CancellationTokenSource? _auditCancellation;
    private string _auditStatus =
        "Expand to load durable, secret-free action evidence for this run.";
    private AgentPermission _terminalMutationPermission = AgentPermission.Ask;
    private bool _terminalMutationAvailable;
    private bool _runtimeCanSend = true;
    private bool _runtimeCanSteer;
    private long? _steeringGeneration;
    private bool _runtimeCanStop;
    private bool _isRunBound;
    private bool _isExactTerminalPanelRun;
    private bool _isContextInspectorExpanded;
    private bool _isAuditExpanded;
    private bool _isAuditLoading;
    private bool _actionCancellationInFlight;
    private bool _decisionInFlight;
    private bool _questionResponseInFlight;
    private bool _capabilityDecisionInFlight;
    private bool _policyChangeInFlight;
    private bool _stopInFlight;
    private bool _steerInFlight;
    private bool _clearInFlight;
    private bool _disposed;

    public AgentChatViewModel(
        IGovernedAgentRuntime runtime,
        IAiProviderProfileRuntime profiles,
        IUiThreadDispatcher dispatcher,
        IAgentRunAuditReader? auditReader = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _auditReader = auditReader;
        _runtime.Changed += OnRuntimeChanged;
        _profiles.ProfilesChanged += OnProfilesChanged;
        Refresh();
    }

    public ObservableCollection<AiProviderProfileDescriptor> Providers { get; } = [];

    public ObservableCollection<AgentChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<AgentContextItemViewModel> ContextItems { get; } = [];

    public ObservableCollection<AgentPolicyCapabilityViewModel> EffectivePolicyCapabilities
    { get; } = [];

    public ObservableCollection<AgentAuditEntryViewModel> AuditEntries { get; } = [];

    public AiProviderProfileDescriptor? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                NotifyAvailabilityChanged();
            }
        }
    }

    public string Prompt
    {
        get => _prompt;
        set
        {
            if (SetProperty(ref _prompt, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanSteer));
                OnPropertyChanged(nameof(CanSubmitPrompt));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ProvisionalAssistantText
    {
        get => _provisionalAssistantText;
        private set
        {
            if (SetProperty(ref _provisionalAssistantText, value))
            {
                NotifyContentChanged();
            }
        }
    }

    public GovernedAgentState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsStreaming));
                OnPropertyChanged(nameof(CanCancelActiveAction));
                NotifyQuestionAvailabilityChanged();
                NotifyCapabilityRequestAvailabilityChanged();
            }
        }
    }

    public string StateLabel => State switch
    {
        GovernedAgentState.Ready => "Ready",
        GovernedAgentState.StreamingProvider => "Thinking",
        GovernedAgentState.AwaitingUserInput => "Input needed",
        GovernedAgentState.AwaitingCapabilityDecision => "Capability request",
        GovernedAgentState.AwaitingApproval => "Approval",
        GovernedAgentState.RunningTool => "Tool active",
        GovernedAgentState.Cancelling => "Stopping",
        GovernedAgentState.Failed => "Failed",
        GovernedAgentState.Cancelled => "Stopped",
        _ => "Unknown",
    };

    public bool IsBusy => State is
        GovernedAgentState.StreamingProvider
        or GovernedAgentState.AwaitingUserInput
        or GovernedAgentState.AwaitingCapabilityDecision
        or GovernedAgentState.AwaitingApproval
        or GovernedAgentState.RunningTool
        or GovernedAgentState.Cancelling;

    // Preserved for the existing send-button binding name.
    public bool IsStreaming => IsBusy;

    public string TargetTitle
    {
        get => _targetTitle;
        private set
        {
            if (SetProperty(ref _targetTitle, value))
            {
                OnPropertyChanged(nameof(ScopeLabel));
            }
        }
    }

    public string ExactTarget
    {
        get => _exactTarget;
        private set
        {
            if (SetProperty(ref _exactTarget, value))
            {
                OnPropertyChanged(nameof(HasExactTarget));
                OnPropertyChanged(nameof(ScopeLabel));
            }
        }
    }

    public bool HasExactTarget => ExactTarget.Length > 0;

    public string ScopeLabel => HasExactTarget
        ? $"acting in · {TargetTitle}"
        : "Select an active panel";

    public string ConnectionBoundary
    {
        get => _connectionBoundary;
        private set
        {
            if (SetProperty(ref _connectionBoundary, value))
            {
                OnPropertyChanged(nameof(HasTargetContext));
                OnPropertyChanged(nameof(TargetContextLabel));
            }
        }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        private set
        {
            if (SetProperty(ref _workingDirectory, value))
            {
                OnPropertyChanged(nameof(HasTargetContext));
                OnPropertyChanged(nameof(TargetContextLabel));
            }
        }
    }

    public bool HasTargetContext =>
        ConnectionBoundary.Length > 0 || WorkingDirectory.Length > 0;

    public string TargetContextLabel =>
        (ConnectionBoundary.Length > 0, WorkingDirectory.Length > 0) switch
        {
            (true, true) => $"{ConnectionBoundary} · {WorkingDirectory}",
            (true, false) => ConnectionBoundary,
            (false, true) => WorkingDirectory,
            _ => string.Empty,
        };

    public bool HasContextItems => ContextItems.Count > 0;

    public string ContextInspectorSummary
    {
        get
        {
            var terminalCount =
                ContextItems.Count(item => item.Kind == PanelKind.Terminal);
            var browserCount =
                ContextItems.Count(item => item.Kind == PanelKind.Browser);
            var fileCount =
                ContextItems.Count(item => item.Kind == PanelKind.FileViewer);
            var processMonitorCount =
                ContextItems.Count(item => item.Kind == PanelKind.ProcessMonitor);
            if (terminalCount == ContextItems.Count)
            {
                return terminalCount == 1
                    ? "1 terminal"
                    : $"{terminalCount.ToString(CultureInfo.InvariantCulture)} terminals";
            }

            if (browserCount == ContextItems.Count)
            {
                return browserCount == 1
                    ? "1 browser"
                    : $"{browserCount.ToString(CultureInfo.InvariantCulture)} browsers";
            }

            if (fileCount == ContextItems.Count)
            {
                return fileCount == 1
                    ? "1 File Viewer"
                    : $"{fileCount.ToString(CultureInfo.InvariantCulture)} File Viewers";
            }

            if (processMonitorCount == ContextItems.Count)
            {
                return processMonitorCount == 1
                    ? "1 Process Monitor"
                    : $"{processMonitorCount.ToString(CultureInfo.InvariantCulture)} Process Monitors";
            }

            return $"{ContextItems.Count.ToString(CultureInfo.InvariantCulture)} panels";
        }
    }

    public string ContextInspectorAccessibleName =>
        $"Inspect agent context · {ContextInspectorSummary}";

    public bool IsContextInspectorExpanded
    {
        get => _isContextInspectorExpanded;
        set => SetProperty(ref _isContextInspectorExpanded, value);
    }

    public bool IsAuditExpanded
    {
        get => _isAuditExpanded;
        set
        {
            if (!SetProperty(ref _isAuditExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRefreshAudit));
            if (value && CanShowAudit && AuditEntries.Count == 0)
            {
                _ = RefreshAuditAsync(_lifetime.Token);
            }
        }
    }

    public bool CanShowAudit =>
        _auditReader is not null && _auditRunId is not null;

    public bool HasAuditEntries => AuditEntries.Count > 0;

    public bool IsAuditLoading
    {
        get => _isAuditLoading;
        private set
        {
            if (SetProperty(ref _isAuditLoading, value))
            {
                OnPropertyChanged(nameof(CanRefreshAudit));
                OnPropertyChanged(nameof(CanLoadOlderAudit));
                OnPropertyChanged(nameof(AuditSummary));
            }
        }
    }

    public string AuditStatus
    {
        get => _auditStatus;
        private set => SetProperty(ref _auditStatus, value);
    }

    public string AuditSummary => IsAuditLoading
        ? "Loading"
        : AuditEntries.Count == 0
            ? "No actions"
            : $"{AuditEntries.Count.ToString(CultureInfo.InvariantCulture)} "
              + (AuditEntries.Count == 1 ? "entry" : "entries");

    public bool CanRefreshAudit =>
        CanShowAudit && IsAuditExpanded && !IsAuditLoading;

    public bool CanLoadOlderAudit =>
        CanRefreshAudit && _nextAuditCursor is not null;

    public string CapabilityNotice
    {
        get => _capabilityNotice;
        private set
        {
            if (SetProperty(ref _capabilityNotice, value))
            {
                OnPropertyChanged(nameof(HasCapabilityNotice));
            }
        }
    }

    /// <summary>
    /// The capability card describes what a run would be allowed to do. With no
    /// provider configured there is no run to describe, and the card only competed
    /// with the empty state that explains how to get one.
    /// </summary>
    public bool HasCapabilityNotice => HasProvider && CapabilityNotice.Length > 0;

    public bool TerminalMutationAvailable
    {
        get => _terminalMutationAvailable;
        private set
        {
            if (SetProperty(ref _terminalMutationAvailable, value))
            {
                OnPropertyChanged(nameof(CapabilityLabel));
            }
        }
    }

    public string CapabilityLabel => TerminalMutationAvailable
        ? "Governed input"
        : "Capability check";

    public string EffectivePolicyProvider
    {
        get => _effectivePolicyProvider;
        private set
        {
            if (SetProperty(ref _effectivePolicyProvider, value))
            {
                OnPropertyChanged(nameof(EffectivePolicySummary));
            }
        }
    }

    public string EffectivePolicyModel
    {
        get => _effectivePolicyModel;
        private set
        {
            if (SetProperty(ref _effectivePolicyModel, value))
            {
                OnPropertyChanged(nameof(EffectivePolicySummary));
            }
        }
    }

    public string EffectivePolicySummary =>
        $"{EffectivePolicyProvider} · {EffectivePolicyModel}";

    public string RendererModeDescription =>
        "Native .NET · governed panel-scope agent";

    public AgentApprovalCardViewModel? PendingApproval
    {
        get => _pendingApproval;
        private set
        {
            if (SetProperty(ref _pendingApproval, value))
            {
                OnPropertyChanged(nameof(HasPendingApproval));
                OnPropertyChanged(nameof(CanDecideApproval));
                NotifyContentChanged();
            }
        }
    }

    public bool HasPendingApproval => PendingApproval is not null;

    public bool CanDecideApproval =>
        PendingApproval is not null
        && State == GovernedAgentState.AwaitingApproval
        && !_decisionInFlight;

    public AgentQuestionCardViewModel? PendingQuestion
    {
        get => _pendingQuestion;
        private set
        {
            if (SetProperty(ref _pendingQuestion, value))
            {
                OnPropertyChanged(nameof(HasPendingQuestion));
                NotifyQuestionAvailabilityChanged();
                NotifyContentChanged();
            }
        }
    }

    public bool HasPendingQuestion => PendingQuestion is not null;

    public string QuestionResponseDraft
    {
        get => _questionResponseDraft;
        set
        {
            if (SetProperty(ref _questionResponseDraft, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSubmitQuestionResponse));
            }
        }
    }

    public bool CanRespondToQuestion =>
        PendingQuestion is not null
        && State == GovernedAgentState.AwaitingUserInput
        && !_questionResponseInFlight;

    public bool CanSubmitQuestionResponse =>
        CanRespondToQuestion
        && !string.IsNullOrWhiteSpace(QuestionResponseDraft);

    public bool CanDeclineQuestion => CanRespondToQuestion;

    public AgentCapabilityRequestCardViewModel? PendingCapabilityRequest
    {
        get => _pendingCapabilityRequest;
        private set
        {
            if (SetProperty(ref _pendingCapabilityRequest, value))
            {
                OnPropertyChanged(nameof(HasPendingCapabilityRequest));
                NotifyCapabilityRequestAvailabilityChanged();
                NotifyContentChanged();
            }
        }
    }

    public bool HasPendingCapabilityRequest =>
        PendingCapabilityRequest is not null;

    public bool CanDecideCapabilityRequest =>
        PendingCapabilityRequest is not null
        && State == GovernedAgentState.AwaitingCapabilityDecision
        && !_capabilityDecisionInFlight;

    public AgentToolActivityViewModel? ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (SetProperty(ref _activeTool, value))
            {
                OnPropertyChanged(nameof(HasActiveTool));
                OnPropertyChanged(nameof(CanCancelActiveAction));
                OnPropertyChanged(nameof(ActiveActionCancellationLabel));
                NotifyContentChanged();
            }
        }
    }

    public bool HasActiveTool => ActiveTool is not null;

    public AgentProgressViewModel? CurrentProgress
    {
        get => _currentProgress;
        private set
        {
            if (SetProperty(ref _currentProgress, value))
            {
                OnPropertyChanged(nameof(HasCurrentProgress));
                NotifyContentChanged();
            }
        }
    }

    public bool HasCurrentProgress => CurrentProgress is not null;

    public bool CanCancelActiveAction =>
        State == GovernedAgentState.RunningTool
        && ActiveTool is { CancellationRequested: false }
        && !_actionCancellationInFlight;

    public string ActiveActionCancellationLabel =>
        _actionCancellationInFlight
        || ActiveTool is { CancellationRequested: true }
            ? "Cancelling action…"
            : "Cancel action";

    public AgentYoloAuthorityViewModel? YoloAuthority
    {
        get => _yoloAuthority;
        private set
        {
            if (SetProperty(ref _yoloAuthority, value))
            {
                OnPropertyChanged(nameof(HasYoloAuthority));
                OnPropertyChanged(nameof(CanOfferYolo));
                OnPropertyChanged(nameof(CanEnableYolo));
                OnPropertyChanged(nameof(CanDisableYolo));
                OnPropertyChanged(nameof(PolicyModeLabel));
                NotifyContentChanged();
            }
        }
    }

    public bool HasYoloAuthority => YoloAuthority is not null;

    public bool CanOfferYolo =>
        !HasYoloAuthority
        && _isRunBound
        && _isExactTerminalPanelRun
        && HasExactTarget
        && State == GovernedAgentState.Ready;

    public bool CanEnableYolo => CanOfferYolo && !_policyChangeInFlight;

    public bool CanDisableYolo =>
        HasYoloAuthority
        && !_policyChangeInFlight
        && State != GovernedAgentState.Cancelled;

    public string PolicyModeLabel => HasYoloAuthority
        ? "YOLO"
        : FormatEnum(_terminalMutationPermission);

    public bool HasProvider => Providers.Count > 0;

    public bool HasNoProvider => !HasProvider;

    public bool HasConversation => Messages.Count > 0 || HasProvisionalAssistantText;

    public bool HasAgentContent =>
        HasConversation
        || HasPendingQuestion
        || HasPendingCapabilityRequest
        || HasPendingApproval
        || HasActiveTool
        || HasCurrentProgress
        || CanShowAudit;

    public bool HasNoConversation => !HasAgentContent;

    public bool CanStartConversation =>
        SelectedProvider is not null
        && State == GovernedAgentState.Ready
        && HasNoConversation
        && !_isRunBound;

    public bool HasProvisionalAssistantText => ProvisionalAssistantText.Length > 0;

    public bool CanSend =>
        SelectedProvider is not null
        && _runtimeCanSend
        && State == GovernedAgentState.Ready
        && !_clearInFlight
        && !string.IsNullOrWhiteSpace(Prompt);

    public bool IsSteeringAvailable =>
        SelectedProvider is not null
        && _runtimeCanSteer
        && _steeringGeneration is > 0
        && _runId is not null
        && State == GovernedAgentState.StreamingProvider
        && !_clearInFlight
        && !_steerInFlight;

    public bool CanSteer =>
        IsSteeringAvailable
        && !string.IsNullOrWhiteSpace(Prompt);

    public bool CanSubmitPrompt => CanSend || CanSteer;

    public bool CanShowPrimaryAction => !IsStreaming || IsSteeringAvailable;

    public string PrimaryActionLabel =>
        IsSteeringAvailable ? "Steer" : "Send";

    public string PrimaryActionAccessibleName =>
        IsSteeringAvailable
            ? "Steer the current AI agent response"
            : "Send AI agent prompt";

    public string PromptPlaceholder =>
        IsSteeringAvailable
            ? "Steer the current response…"
            : "Ask GhostSHELL…";

    // Preserved as an alias for callers that still use the old chat naming.
    public bool CanCancel => CanStop;

    public bool CanStop => _runtimeCanStop || IsBusy;

    public bool CanRequestStop =>
        CanStop && !_stopInFlight && State != GovernedAgentState.Cancelling;

    public bool CanClear =>
        !IsBusy
        && !_clearInFlight
        && (HasConversation
            || _isRunBound
            || State is GovernedAgentState.Failed or GovernedAgentState.Cancelled);

    public bool CanChangeProvider =>
        State == GovernedAgentState.Ready
        && !_isRunBound
        && !HasConversation
        && !_clearInFlight;

    public bool CanEnterPrompt =>
        SelectedProvider is not null
        && !_clearInFlight
        && ((_runtimeCanSend && State == GovernedAgentState.Ready)
            || IsSteeringAvailable);

    public bool NeedsProviderAttention =>
        SelectedProvider is null
        || State is GovernedAgentState.Failed
            or GovernedAgentState.AwaitingUserInput
            or GovernedAgentState.AwaitingCapabilityDecision
            or GovernedAgentState.AwaitingApproval;

    public string ConnectionStatus => SelectedProvider is null
        ? HasProvider && _isRunBound
            ? "Clear required"
            : "Not connected"
        : StateLabel;

    public Task SendAsync(
        AgentTarget target,
        CancellationToken cancellationToken) =>
        SendWithPolicyAsync(target, policy: null, cancellationToken);

    public async Task SteerAsync(CancellationToken cancellationToken)
    {
        if (!CanSteer
            || _runId is not { } runId
            || _steeringGeneration is not { } steeringGeneration)
        {
            return;
        }

        var update = Prompt;
        _steerInFlight = true;
        Prompt = string.Empty;
        NotifyAvailabilityChanged();
        try
        {
            var result = await _runtime.SteerAsync(
                new GovernedAgentSteering(
                    runId,
                    steeringGeneration,
                    update),
                cancellationToken);
            if (!result.IsAccepted)
            {
                if (string.IsNullOrEmpty(Prompt))
                {
                    Prompt = update;
                }

                Status = result.Message;
            }
        }
        catch
        {
            if (string.IsNullOrEmpty(Prompt))
            {
                Prompt = update;
            }

            throw;
        }
        finally
        {
            _steerInFlight = false;
            NotifyAvailabilityChanged();
        }
    }

    public Task SendAsync(
        AgentTarget target,
        AgentPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return SendWithPolicyAsync(target, policy, cancellationToken);
    }

    private Task SendWithPolicyAsync(
        AgentTarget target,
        AgentPolicy? policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_runtimeCanSend
            || State != GovernedAgentState.Ready
            || _clearInFlight
            || string.IsNullOrWhiteSpace(Prompt))
        {
            return Task.CompletedTask;
        }

        var provider = policy is null
            ? SelectedProvider
            : Providers.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.Id.Value,
                    policy.Provider,
                    StringComparison.Ordinal));
        if (provider is null)
        {
            ReportTargetUnavailable(policy is null
                ? "Choose an enabled AI-provider profile."
                : "The saved agent policy references an unavailable AI-provider profile.");
            return Task.CompletedTask;
        }

        if (policy is not null)
        {
            SelectedProvider = provider;
        }

        var prompt = Prompt;
        var request = policy is null
            ? new GovernedAgentPrompt(
                provider.Id,
                prompt,
                target)
            : new GovernedAgentPrompt(
                provider.Id,
                prompt,
                target,
                policy);
        Prompt = string.Empty;
        lock (_sendGate)
        {
            if (!_activeSend.IsCompleted)
            {
                Prompt = prompt;
                return Task.CompletedTask;
            }

            _activeSend = SendCoreAsync(request, prompt, cancellationToken);
            return _activeSend;
        }
    }

    public async Task DecideAsync(bool approved, CancellationToken cancellationToken)
    {
        if (!CanDecideApproval || PendingApproval is not { } approval)
        {
            return;
        }

        _decisionInFlight = true;
        OnPropertyChanged(nameof(CanDecideApproval));
        try
        {
            var result = await _runtime
                .DecideAsync(approval.Id, approved, cancellationToken);
            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _decisionInFlight = false;
            OnPropertyChanged(nameof(CanDecideApproval));
        }
    }

    public Task SubmitQuestionResponseAsync(
        CancellationToken cancellationToken)
    {
        if (!CanSubmitQuestionResponse || PendingQuestion is not { } question)
        {
            return Task.CompletedTask;
        }

        GovernedAgentQuestionResponse.Submitted response;
        try
        {
            response = new GovernedAgentQuestionResponse.Submitted(
                QuestionResponseDraft);
        }
        catch (ArgumentException)
        {
            Status =
                "Enter a printable single-line answer without passwords, tokens, "
                + "private keys, or other credentials.";
            return Task.CompletedTask;
        }

        return RespondToQuestionAsync(question, response, cancellationToken);
    }

    public Task DeclineQuestionAsync(CancellationToken cancellationToken)
    {
        if (!CanDeclineQuestion || PendingQuestion is not { } question)
        {
            return Task.CompletedTask;
        }

        return RespondToQuestionAsync(
            question,
            new GovernedAgentQuestionResponse.Declined(),
            cancellationToken);
    }

    public Task EnableCapabilityAskAsync(CancellationToken cancellationToken)
    {
        if (!CanDecideCapabilityRequest
            || PendingCapabilityRequest is not { } request)
        {
            return Task.CompletedTask;
        }

        return DecideCapabilityRequestAsync(
            request,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            cancellationToken);
    }

    public Task KeepCapabilityOffAsync(CancellationToken cancellationToken)
    {
        if (!CanDecideCapabilityRequest
            || PendingCapabilityRequest is not { } request)
        {
            return Task.CompletedTask;
        }

        return DecideCapabilityRequestAsync(
            request,
            new GovernedAgentCapabilityDecision.KeepOff(),
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!CanStop || _stopInFlight)
        {
            return;
        }

        _stopInFlight = true;
        OnPropertyChanged(nameof(CanRequestStop));
        try
        {
            var result = await _runtime.StopAsync(cancellationToken);
            if (!result.WasRunning)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _stopInFlight = false;
            OnPropertyChanged(nameof(CanRequestStop));
        }
    }

    public async Task CancelActiveActionAsync(
        CancellationToken cancellationToken)
    {
        if (!CanCancelActiveAction)
        {
            return;
        }

        _actionCancellationInFlight = true;
        NotifyActionCancellationChanged();
        try
        {
            var result = await _runtime
                .CancelActiveActionAsync(cancellationToken);
            if (!result.WasRequested)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _actionCancellationInFlight = false;
            NotifyActionCancellationChanged();
        }
    }

    public async Task EnableYoloAsync(
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (!CanEnableYolo)
        {
            return;
        }

        _policyChangeInFlight = true;
        NotifyPolicyAvailabilityChanged();
        try
        {
            var result = await _runtime.EnableYoloAsync(
                lifetime,
                cancellationToken);
            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _policyChangeInFlight = false;
            NotifyPolicyAvailabilityChanged();
        }
    }

    public async Task DisableYoloAsync(CancellationToken cancellationToken)
    {
        if (!CanDisableYolo)
        {
            return;
        }

        _policyChangeInFlight = true;
        NotifyPolicyAvailabilityChanged();
        try
        {
            var result = await _runtime.DisableYoloAsync(cancellationToken);
            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _policyChangeInFlight = false;
            NotifyPolicyAvailabilityChanged();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        if (!CanClear || _clearInFlight)
        {
            return;
        }

        _clearInFlight = true;
        NotifyAvailabilityChanged();
        try
        {
            if (!await _runtime.ClearAsync(cancellationToken))
            {
                Status = "The agent run could not be cleared while work is still active.";
            }
        }
        finally
        {
            _clearInFlight = false;
            NotifyAvailabilityChanged();
        }
    }

    public Task RefreshAuditAsync(CancellationToken cancellationToken) =>
        LoadAuditAsync(replace: true, cancellationToken);

    public Task LoadOlderAuditAsync(CancellationToken cancellationToken) =>
        LoadAuditAsync(replace: false, cancellationToken);

    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        if (CanStop)
        {
            await StopAsync(cancellationToken);
        }

        Task activeSend;
        lock (_sendGate)
        {
            activeSend = _activeSend;
        }

        await activeSend.WaitAsync(cancellationToken);
    }

    public void Cancel()
    {
        if (!_disposed && CanStop)
        {
            _ = StopForDisposalAsync();
        }
    }

    internal void ReportTargetUnavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Status = message;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Changed -= OnRuntimeChanged;
        _profiles.ProfilesChanged -= OnProfilesChanged;
        _auditCancellation?.Cancel();
        _auditCancellation?.Dispose();
        _auditCancellation = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task SendCoreAsync(
        GovernedAgentPrompt request,
        string prompt,
        CancellationToken cancellationToken)
    {
        var result = await _runtime.SendAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            if (string.IsNullOrEmpty(Prompt))
            {
                Prompt = prompt;
            }

            Status = result.Message;
        }
    }

    private async Task RespondToQuestionAsync(
        AgentQuestionCardViewModel question,
        GovernedAgentQuestionResponse response,
        CancellationToken cancellationToken)
    {
        _questionResponseInFlight = true;
        NotifyQuestionAvailabilityChanged();
        try
        {
            var result = await _runtime.RespondToQuestionAsync(
                question.Id,
                response,
                cancellationToken);
            if (result.IsAccepted || IsStaleQuestionResponse(result.Code))
            {
                ClearQuestionIfCurrent(question.Id);
                QuestionResponseDraft = string.Empty;
            }

            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _questionResponseInFlight = false;
            NotifyQuestionAvailabilityChanged();
        }
    }

    private async Task DecideCapabilityRequestAsync(
        AgentCapabilityRequestCardViewModel request,
        GovernedAgentCapabilityDecision decision,
        CancellationToken cancellationToken)
    {
        _capabilityDecisionInFlight = true;
        NotifyCapabilityRequestAvailabilityChanged();
        try
        {
            var result = await _runtime.DecideCapabilityRequestAsync(
                request.Id,
                decision,
                cancellationToken);
            if (result.IsAccepted
                || IsStaleCapabilityDecision(result.Code))
            {
                ClearCapabilityRequestIfCurrent(request.Id);
            }

            if (!result.IsAccepted)
            {
                Status = result.Message;
            }
        }
        finally
        {
            _capabilityDecisionInFlight = false;
            NotifyCapabilityRequestAvailabilityChanged();
        }
    }

    private async Task StopForDisposalAsync()
    {
        try
        {
            await _runtime.StopAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueRefresh();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueRefresh();
    }

    private void QueueRefresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(Refresh, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void Refresh()
    {
        var snapshot = _runtime.Snapshot;
        var previousState = State;
        var previousQuestionId = PendingQuestion?.Id;
        var selectedId = snapshot.ProviderId ?? SelectedProvider?.Id;
        var hadProvider = HasProvider;
        Replace(
            Providers,
            _profiles.Profiles
                .Where(profile => profile.IsEnabled)
                .OrderBy(profile => profile.Order)
                .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase));
        if (hadProvider != HasProvider)
        {
            OnPropertyChanged(nameof(HasProvider));
            OnPropertyChanged(nameof(HasNoProvider));
        }

        var selectedProvider = Providers.FirstOrDefault(profile => profile.Id == selectedId);
        var isRunBound = snapshot.RunId is not null
            || snapshot.ProviderId is not null && snapshot.HasMessages;
        var boundProviderMissing = snapshot.ProviderId is not null
            && isRunBound
            && selectedProvider is null;
        SelectedProvider = selectedProvider
            ?? (boundProviderMissing ? null : Providers.FirstOrDefault());

        Replace(
            Messages,
            snapshot.Messages.Select(message =>
                new AgentChatMessageViewModel(message.Role, message.Content)));
        Replace(
            ContextItems,
            snapshot.ContextItems.Select(CreateContextItem));
        OnPropertyChanged(nameof(HasContextItems));
        OnPropertyChanged(nameof(ContextInspectorSummary));
        OnPropertyChanged(nameof(ContextInspectorAccessibleName));
        if (!HasContextItems)
        {
            IsContextInspectorExpanded = false;
        }

        ProvisionalAssistantText = snapshot.ProvisionalAssistantText;
        CurrentProgress = snapshot.CurrentProgress is { } progress
            ? new AgentProgressViewModel(progress)
            : null;
        State = snapshot.State;
        if (!_questionResponseInFlight
            && previousQuestionId != snapshot.PendingQuestion?.Id)
        {
            QuestionResponseDraft = string.Empty;
        }

        PendingQuestion = snapshot.PendingQuestion is { } question
            ? new AgentQuestionCardViewModel(question)
            : null;
        PendingCapabilityRequest = CreateCapabilityRequest(
            snapshot.PendingCapabilityRequest);
        var auditRunChanged = _auditRunId != snapshot.RunId;
        if (auditRunChanged)
        {
            ResetAudit(snapshot.RunId);
        }

        TargetTitle = snapshot.TargetTitle;
        ExactTarget = FormatTarget(snapshot.Target);
        ConnectionBoundary = snapshot.ConnectionBoundary ?? string.Empty;
        WorkingDirectory = snapshot.WorkingDirectory ?? string.Empty;
        var effectivePolicy = snapshot.EffectivePolicy ?? AgentPolicy.Default;
        EffectivePolicyProvider = effectivePolicy.Provider;
        EffectivePolicyModel = effectivePolicy.Model;
        Replace(
            EffectivePolicyCapabilities,
            AgentPolicy.Capabilities.Select(capability =>
                new AgentPolicyCapabilityViewModel(
                    AgentPolicyPresentation.CapabilityName(capability),
                    AgentPolicyPresentation.PermissionName(
                        effectivePolicy.GetPermission(capability)))));
        TerminalMutationAvailable = snapshot.TerminalMutationAvailable;
        _isExactTerminalPanelRun = snapshot.Target is AgentTarget.Panel
            && (snapshot.ContextItems.Length == 0
                || snapshot.ContextItems is
                [
                {
                    Kind: PanelKind.Terminal,
                },
                ]);
        CapabilityNotice = ResolveCapabilityNotice(snapshot);
        PendingApproval = CreateApproval(snapshot.PendingApproval);
        ActiveTool = snapshot.ActiveTool is null
            ? null
            : new AgentToolActivityViewModel(
                snapshot.ActiveTool.ToolName,
                snapshot.ActiveTool.ToolTitle,
                FormatEnum(snapshot.ActiveTool.Risk),
                snapshot.ActiveTool.TargetTitle,
                snapshot.ActiveTool.CancellationRequested);
        _terminalMutationPermission = snapshot.TerminalMutationPermission;
        YoloAuthority = CreateYoloAuthority(snapshot.YoloAuthority);

        _runId = snapshot.RunId;
        _runtimeCanSend = snapshot.CanSend;
        _runtimeCanSteer = snapshot.CanSteer;
        _steeringGeneration = snapshot.SteeringGeneration;
        _runtimeCanStop = snapshot.CanStop;
        _isRunBound = isRunBound;
        Status = boundProviderMissing
            ? "This run's provider is no longer enabled. Clear the run to choose another."
            : snapshot.Status;
        NotifyAvailabilityChanged();
        NotifyContentChanged();
        if (IsAuditExpanded
            && CanShowAudit
            && (auditRunChanged
                || (previousState != snapshot.State
                    && snapshot.State is
                        GovernedAgentState.Ready
                        or GovernedAgentState.Failed
                        or GovernedAgentState.Cancelled)))
        {
            _ = RefreshAuditAsync(_lifetime.Token);
        }
    }

    private async Task LoadAuditAsync(
        bool replace,
        CancellationToken cancellationToken)
    {
        if (_auditReader is null
            || _auditRunId is not { } runId
            || IsAuditLoading
            || !IsAuditExpanded
            || (!replace && _nextAuditCursor is null))
        {
            return;
        }

        _auditCancellation?.Dispose();
        _auditCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationCancellation = _auditCancellation;
        var cursor = replace ? null : _nextAuditCursor;
        IsAuditLoading = true;
        AuditStatus = replace
            ? "Loading durable action evidence…"
            : "Loading older durable action evidence…";

        AuditStoreResult<AgentRunAuditPage>? result = null;
        try
        {
            result = await _auditReader.ReadAsync(
                new AgentRunAuditQuery(runId, cursor),
                operationCancellation.Token);
        }
        catch (OperationCanceledException)
            when (operationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            if (_auditRunId == runId)
            {
                AuditStatus = "Durable action evidence is temporarily unavailable.";
            }
        }
        finally
        {
            if (ReferenceEquals(_auditCancellation, operationCancellation))
            {
                _auditCancellation = null;
                operationCancellation.Dispose();
                IsAuditLoading = false;
            }
        }

        if (result is null || _auditRunId != runId)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            AuditStatus = AuditFailureStatus(result.Error!.Code);
            return;
        }

        var page = result.Value!;
        if (replace)
        {
            Replace(
                AuditEntries,
                page.Entries.Select(CreateAuditEntry));
        }
        else
        {
            foreach (var entry in page.Entries)
            {
                AuditEntries.Add(CreateAuditEntry(entry));
            }
        }

        _nextAuditCursor = page.Next;
        NotifyAuditContentChanged();
        AuditStatus = AuditEntries.Count == 0
            ? "No governed actions have durable audit evidence in this run yet."
            : _nextAuditCursor is null
                ? "Showing all durable action evidence for this run."
                : "Showing the newest durable action evidence. Older entries are available.";
    }

    private void ResetAudit(AgentRunId? runId)
    {
        _auditCancellation?.Cancel();
        _auditCancellation?.Dispose();
        _auditCancellation = null;
        _auditRunId = runId;
        _nextAuditCursor = null;
        AuditEntries.Clear();
        IsAuditLoading = false;
        if (runId is null)
        {
            IsAuditExpanded = false;
        }

        AuditStatus = runId is null
            ? "Start a governed run to inspect its durable action evidence."
            : "Expand to load durable, secret-free action evidence for this run.";
        OnPropertyChanged(nameof(CanShowAudit));
        NotifyAuditContentChanged();
        NotifyContentChanged();
    }

    private static AgentAuditEntryViewModel CreateAuditEntry(
        AgentRunAuditEntry entry) =>
        entry switch
        {
            AgentRunAuditActionEntry action => CreateActionAuditEntry(action),
            AgentRunAuditPolicyEntry policy => CreatePolicyAuditEntry(policy),
            _ => throw new ArgumentOutOfRangeException(
                nameof(entry),
                entry.GetType(),
                "The agent audit entry kind is unsupported."),
        };

    private static AgentAuditEntryViewModel CreateActionAuditEntry(
        AgentRunAuditActionEntry action)
    {
        var title = BuiltInAgentTools.Catalog.TryGet(
                action.ToolName,
                out var descriptor)
            ? descriptor!.Title
            : action.ToolName;
        var outcome = FormatEnum(action.LatestOutcome);
        var authorization = action.AuthorizationSource is { } source
            ? $" · {FormatEnum(source)}"
            : string.Empty;
        var evidence =
            $"{FormatEnum(action.Capability)} · {FormatEnum(action.Permission)}"
            + $" · {FormatEnum(action.Risk)}{authorization}";
        var timeline = string.Join(
            " → ",
            action.Phases.Select(phase => FormatEnum(phase.Outcome)));
        var result = AuditActionResult(action);
        var target = $"Verified target · {ShortDigest(action.TargetIdentity)}";
        var occurred = LocalAuditTime(action.OccurredAtUtc);
        return new AgentAuditEntryViewModel(
            "Action",
            title,
            action.ToolName,
            outcome,
            evidence,
            timeline,
            result,
            target,
            occurred,
            $"{title}; {outcome}; {evidence}; phases {timeline}; "
            + $"{result}; {target}; {occurred}");
    }

    private static AgentAuditEntryViewModel CreatePolicyAuditEntry(
        AgentRunAuditPolicyEntry policy)
    {
        var transition = policy.Transition switch
        {
            AgentRunPolicyTransition.YoloEnabled => "YOLO enabled",
            AgentRunPolicyTransition.YoloDisabled => "YOLO disabled",
            AgentRunPolicyTransition.YoloExpired => "YOLO expired",
            AgentRunPolicyTransition.Updated => "Run policy updated",
            _ => throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.Transition,
                "The policy transition is unsupported."),
        };
        var evidence =
            $"Policy generation {policy.PolicyGeneration.ToString(CultureInfo.InvariantCulture)}";
        var result = policy.YoloExpiresAtUtc is { } expiry
            ? $"Authority window ends {LocalAuditTime(expiry)}"
            : string.Empty;
        var target = $"Verified target · {ShortDigest(policy.TargetIdentity)}";
        var occurred = LocalAuditTime(policy.OccurredAtUtc);
        return new AgentAuditEntryViewModel(
            "Policy",
            transition,
            string.Empty,
            "Succeeded",
            evidence,
            "Succeeded",
            result,
            target,
            occurred,
            $"{transition}; {evidence}; {result}; {target}; {occurred}");
    }

    private static string AuditActionResult(AgentRunAuditActionEntry action)
    {
        var values = new List<string>(3);
        if (action.ResultCode is { } resultCode)
        {
            values.Add($"Result · {resultCode}");
        }
        else if (action.ErrorCode is { } errorCode)
        {
            values.Add($"Error · {FormatEnum(errorCode)}");
        }

        if (action.ExecutionDurationMilliseconds is { } duration)
        {
            values.Add(
                $"Duration · {duration.ToString(CultureInfo.InvariantCulture)} ms");
        }

        if (action.ResultCount is { } count)
        {
            values.Add(
                $"Count · {count.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(" · ", values);
    }

    private static string ShortDigest(AgentActionDigest digest) =>
        $"{digest.Value[..12]}…";

    private static string LocalAuditTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss zzz",
            CultureInfo.InvariantCulture);

    private static string AuditFailureStatus(AuditStoreErrorCode code) =>
        code switch
        {
            AuditStoreErrorCode.Cancelled =>
                "Loading durable action evidence was cancelled.",
            AuditStoreErrorCode.InvalidQuery =>
                "The audit position changed. Refresh the current run.",
            AuditStoreErrorCode.StorageFailure =>
                "Durable action evidence is invalid and cannot be shown safely.",
            _ => "Durable action evidence is temporarily unavailable.",
        };

    private static bool IsStaleQuestionResponse(string code) =>
        code is
            "question_not_found"
            or "question_expired"
            or "question_cancelled"
            or "target_changed";

    private static bool IsStaleCapabilityDecision(string code) =>
        code is
            "capability_request_not_found"
            or "capability_request_expired"
            or "capability_request_cancelled"
            or "capability_request_unavailable"
            or "policy_changed"
            or "target_changed";

    private void ClearQuestionIfCurrent(AgentQuestionId questionId)
    {
        if (PendingQuestion?.Id == questionId)
        {
            PendingQuestion = null;
        }
    }

    private void ClearCapabilityRequestIfCurrent(
        AgentCapabilityRequestId requestId)
    {
        if (PendingCapabilityRequest?.Id == requestId)
        {
            PendingCapabilityRequest = null;
        }
    }

    private void NotifyAuditContentChanged()
    {
        OnPropertyChanged(nameof(HasAuditEntries));
        OnPropertyChanged(nameof(AuditSummary));
        OnPropertyChanged(nameof(CanLoadOlderAudit));
        OnPropertyChanged(nameof(CanRefreshAudit));
    }

    private static AgentContextItemViewModel CreateContextItem(
        GovernedAgentContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var panelKind = item.Kind switch
        {
            PanelKind.Terminal => "terminal",
            PanelKind.Browser => "browser",
            PanelKind.FileViewer => "File Viewer",
            PanelKind.ProcessMonitor => "Process Monitor",
            _ => "panel",
        };
        var title = item.PanelTitle ?? $"Unnamed {panelKind}";
        var tabTitle = item.TabTitle ?? "Unnamed tab";
        var exactIdentity =
            $"{panelKind} · window/{item.WindowId.Value} · "
            + $"workspace/{item.WorkspaceId.Value} · "
            + $"tab/{item.TabId.Value} · panel/{item.PanelId.Value} · "
            + $"session/{item.SessionId.Value}";
        var context = item.Kind switch
        {
            PanelKind.Browser =>
                "Browser state is available only through governed reads",
            PanelKind.FileViewer
                when item.FileProviderProfileId is { } providerProfile
                     && item.FileRootDisplay is { } trustedRoot =>
                $"{providerProfile} · trusted root {trustedRoot}",
            PanelKind.FileViewer =>
                "No governed File Viewer scope is available",
            PanelKind.ProcessMonitor =>
                "Local host process metadata is available only through governed reads",
            _ => (item.ConnectionBoundary, item.WorkingDirectory) switch
            {
                (not null, not null) =>
                    $"{item.ConnectionBoundary} · {item.WorkingDirectory}",
                (not null, null) => item.ConnectionBoundary,
                (null, not null) => item.WorkingDirectory,
                _ => "Connection and working directory not reported",
            },
        };
        var presence = item.IsFocused
            ? "Focused"
            : item.IsVisible
                ? "Visible"
                : "Background";
        var activeWork = item.HasActiveWork ? " · active work" : string.Empty;
        var state =
            $"{presence} · {FormatEnum(item.Lifecycle)} · "
            + $"{FormatEnum(item.Health)}{activeWork}";
        var operations = item.SupportedOperations.Length == 0
            ? $"No {panelKind} operations"
            : string.Join(" · ", item.SupportedOperations);

        return new AgentContextItemViewModel(
            item.Kind,
            title,
            tabTitle,
            exactIdentity,
            context,
            state,
            operations,
            $"{title}; tab {tabTitle}; {state}; exact identity {exactIdentity}; "
            + $"context {context}; operations {operations}");
    }

    private static string ResolveCapabilityNotice(GovernedAgentSnapshot snapshot)
    {
        var hasTerminal = snapshot.ContextItems.Any(
                item => item.Kind == PanelKind.Terminal)
            || snapshot.ContextItems.Length == 0
            && snapshot.Target is not null;
        var hasBrowser = snapshot.ContextItems.Any(
            item => item.Kind == PanelKind.Browser);
        var hasFiles = snapshot.ContextItems.Any(
            item => item.Kind == PanelKind.FileViewer);
        var hasProcesses = snapshot.ContextItems.Any(
            item => item.Kind == PanelKind.ProcessMonitor);
        var processNotice =
            (snapshot.EffectivePolicy ?? AgentPolicy.Default).GetPermission(
                AgentCapability.ProcessControl) == AgentPermission.Off
                ? "Process observation is local-host-only and disabled by the current policy."
                : "Process metadata is local-host-only and uses governed one-action authorization.";
        var capabilityNotice = !string.IsNullOrWhiteSpace(snapshot.CapabilityNotice)
            ? snapshot.CapabilityNotice
            : hasTerminal && snapshot.TerminalMutationAvailable
                ? "Governed terminal input is available. Physical human input preempts the agent."
                : hasTerminal
                    ? PendingCapabilityNotice
                    : hasBrowser
                        ? "Browser state and navigation use governed one-action authorization."
                        : hasFiles
                            ? "File observations are bounded to the trusted File Viewer root."
                            : hasProcesses
                                ? processNotice
                                : PendingCapabilityNotice;
        if (hasBrowser && hasTerminal)
        {
            capabilityNotice +=
                " Browser state and navigation use governed one-action authorization.";
        }

        if (hasFiles && (hasTerminal || hasBrowser))
        {
            capabilityNotice +=
                " File observations remain bounded to the trusted File Viewer root.";
        }

        if (hasProcesses && (hasTerminal || hasBrowser || hasFiles))
        {
            capabilityNotice += $" {processNotice}";
        }

        if (!hasTerminal
            || snapshot.Target is null or AgentTarget.Panel
            || snapshot.YoloAuthority is not null)
        {
            return capabilityNotice;
        }

        return $"{capabilityNotice} {ExactPanelYoloRequirement}";
    }

    private static AgentApprovalCardViewModel? CreateApproval(
        GovernedAgentApproval? approval)
    {
        if (approval is null)
        {
            return null;
        }

        return new AgentApprovalCardViewModel(
            approval.Id,
            approval.ToolName,
            approval.ToolTitle,
            FormatEnum(approval.Risk),
            FormatEnum(approval.Permission),
            approval.Presentation.TargetTitle,
            FormatTarget(approval.Target),
            approval.Presentation.Host ?? "Not reported",
            approval.Presentation.WorkingDirectory ?? "Not reported",
            approval.Presentation.Arguments
                .Select(argument => new AgentApprovalArgumentViewModel(
                    argument.Name,
                    argument.DisplayValue,
                    argument.IsSensitive))
                .ToArray(),
            AgentPresentationTime.Friendly(approval.ExpiresAtUtc),
            approval.TemporarilyYieldsTerminalInput);
    }

    private static AgentCapabilityRequestCardViewModel? CreateCapabilityRequest(
        GovernedAgentCapabilityRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        return new AgentCapabilityRequestCardViewModel(
            request.Id,
            request.DisplayTitle,
            request.CapabilityToken,
            request.TargetTitle,
            FormatTarget(request.Target),
            request.AffectedToolTitles.ToArray(),
            AgentPresentationTime.Friendly(request.ExpiresAtUtc));
    }

    private static AgentYoloAuthorityViewModel? CreateYoloAuthority(
        GovernedAgentYoloAuthority? authority)
    {
        if (authority is null)
        {
            return null;
        }

        var duration = authority.ExpiresAtUtc - authority.ConfirmedAtUtc;
        return new AgentYoloAuthorityViewModel(
            FormatTarget(authority.Target),
            $"{duration.TotalMinutes:0} min window",
            AgentPresentationTime.Friendly(authority.ExpiresAtUtc));
    }

    private static string FormatTarget(AgentTarget? target) =>
        target switch
        {
            null => string.Empty,
            AgentTarget.Panel panel =>
                $"window/{panel.WindowId.Value} · workspace/{panel.WorkspaceId.Value} · "
                + $"tab/{panel.TabId.Value} · panel/{panel.PanelId.Value}",
            AgentTarget.ConnectionSession session =>
                $"session/{session.SessionId.Value}",
            AgentTarget.OpenTab tab =>
                $"window/{tab.WindowId.Value} · workspace/{tab.WorkspaceId.Value} · "
                + $"tab/{tab.TabId.Value}",
            AgentTarget.Workspace workspace =>
                $"window/{workspace.WindowId.Value} · workspace/{workspace.WorkspaceId.Value}",
            AgentTarget.SelectedPanels selected =>
                $"window/{selected.Panels[0].WindowId.Value} · "
                + $"workspace/{selected.Panels[0].WorkspaceId.Value} · panels ["
                + string.Join(
                    ", ",
                    selected.Panels.Select(panel =>
                        $"{panel.TabId.Value}/{panel.PanelId.Value}"))
                + "]",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The agent target kind is not supported."),
        };

    private static string FormatEnum<T>(T value)
        where T : struct, Enum =>
        value.ToString();

    private void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(IsSteeringAvailable));
        OnPropertyChanged(nameof(CanSteer));
        OnPropertyChanged(nameof(CanSubmitPrompt));
        OnPropertyChanged(nameof(CanShowPrimaryAction));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(PrimaryActionAccessibleName));
        OnPropertyChanged(nameof(PromptPlaceholder));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRequestStop));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanChangeProvider));
        OnPropertyChanged(nameof(CanEnterPrompt));
        OnPropertyChanged(nameof(CanStartConversation));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(NeedsProviderAttention));
        OnPropertyChanged(nameof(HasCapabilityNotice));
        NotifyPolicyAvailabilityChanged();
        NotifyQuestionAvailabilityChanged();
        NotifyCapabilityRequestAvailabilityChanged();
    }

    private void NotifyActionCancellationChanged()
    {
        OnPropertyChanged(nameof(CanCancelActiveAction));
        OnPropertyChanged(nameof(ActiveActionCancellationLabel));
    }

    private void NotifyPolicyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanOfferYolo));
        OnPropertyChanged(nameof(CanEnableYolo));
        OnPropertyChanged(nameof(CanDisableYolo));
        OnPropertyChanged(nameof(PolicyModeLabel));
    }

    private void NotifyQuestionAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanRespondToQuestion));
        OnPropertyChanged(nameof(CanSubmitQuestionResponse));
        OnPropertyChanged(nameof(CanDeclineQuestion));
    }

    private void NotifyCapabilityRequestAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanDecideCapabilityRequest));
    }

    private void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(HasAgentContent));
        OnPropertyChanged(nameof(HasNoConversation));
        OnPropertyChanged(nameof(CanStartConversation));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(HasProvisionalAssistantText));
    }

    private static void Replace<T>(
        ObservableCollection<T> destination,
        IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source)
        {
            destination.Add(item);
        }
    }
}

/// <summary>
/// User-facing time formatting for run-local authority: a person deciding a
/// permission reads "2 Jan 2026, 14:34", not a zoned ISO timestamp. Audit
/// evidence keeps its precise form — that surface is a record, not a prompt.
/// </summary>
internal static class AgentPresentationTime
{
    public static string Friendly(DateTimeOffset value) =>
        value.ToLocalTime().ToString(
            "d MMM yyyy, HH:mm",
            CultureInfo.InvariantCulture);
}
