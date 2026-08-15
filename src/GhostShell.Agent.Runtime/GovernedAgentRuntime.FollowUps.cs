using System.Text;
using GhostShell.Application;

namespace GhostShell.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private const int MaximumQueuedFollowUps = 8;
    private const int MaximumQueuedFollowUpBytes = 256 * 1024;

    private readonly Queue<GovernedAgentFollowUp> _queuedFollowUps = [];
    private int _queuedFollowUpBytes;
    private int _acceptedFollowUpsThisTurn;
    private GovernedAgentFollowUp? _activeFollowUp;
    private bool _activeFollowUpPromptCommitted;
    private bool _initialPromptCommittedThisTurn;

    public ValueTask<GovernedAgentFollowUpResult> QueueFollowUpAsync(
        GovernedAgentFollowUp request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        GovernedAgentFollowUpResult result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turnCancellation is null
                || _session is null
                || !_snapshot.CanQueueFollowUp)
            {
                result = new GovernedAgentFollowUpResult(
                    false,
                    "agent_follow_up_unavailable",
                    "A follow-up can be queued only while a governed turn is active.",
                    _queuedFollowUps.Count);
            }
            else
            {
                var byteCount = Encoding.UTF8.GetByteCount(request.Message);
                if (_acceptedFollowUpsThisTurn >= MaximumQueuedFollowUps
                    || _queuedFollowUps.Count >= MaximumQueuedFollowUps
                    || byteCount > MaximumQueuedFollowUpBytes - _queuedFollowUpBytes)
                {
                    result = new GovernedAgentFollowUpResult(
                        false,
                        "agent_follow_up_queue_full",
                        "The bounded follow-up queue is full.",
                        _queuedFollowUps.Count);
                }
                else
                {
                    _queuedFollowUps.Enqueue(request);
                    _queuedFollowUpBytes = checked(_queuedFollowUpBytes + byteCount);
                    _acceptedFollowUpsThisTurn++;
                    _snapshot = _snapshot with
                    {
                        QueuedFollowUpCount = _queuedFollowUps.Count,
                    };
                    result = new GovernedAgentFollowUpResult(
                        true,
                        "agent_follow_up_queued",
                        "The follow-up will run after the current turn settles.",
                        _queuedFollowUps.Count);
                }
            }
        }

        if (result.IsAccepted)
        {
            NotifyChanged();
        }

        return ValueTask.FromResult(result);
    }

    private void MarkCurrentPromptCommitted()
    {
        lock (_gate)
        {
            if (_activeFollowUp is null)
            {
                _initialPromptCommittedThisTurn = true;
            }
            else
            {
                _activeFollowUpPromptCommitted = true;
            }
        }
    }

    private FollowUpTurnTransition CompleteTurnOrTakeNextFollowUp(
        CancellationTokenSource turnCancellation,
        IReadOnlyList<AgentChatMessage> committedMessages)
    {
        lock (_gate)
        {
            if (_disposed
                || !ReferenceEquals(_turnCancellation, turnCancellation)
                || _snapshot.State == GovernedAgentState.Cancelled)
            {
                return FollowUpTurnTransition.Interrupted;
            }

            _activeFollowUp = null;
            _activeFollowUpPromptCommitted = false;
            if (!_queuedFollowUps.TryDequeue(out var queued))
            {
                _snapshot = _snapshot with
                {
                    State = GovernedAgentState.Ready,
                    Messages = CopyMessages(committedMessages),
                    ProvisionalAssistantText = string.Empty,
                    ProvisionalReasoningSummary = string.Empty,
                    PendingApproval = null,
                    PendingQuestion = null,
                    PendingCapabilityRequest = null,
                    ActiveTool = null,
                    CurrentProgress = null,
                    SteeringAvailable = false,
                    SteeringGeneration = null,
                    Status = string.Empty,
                };
                return FollowUpTurnTransition.Completed;
            }

            _activeFollowUp = queued;
            _queuedFollowUpBytes = checked(
                _queuedFollowUpBytes - Encoding.UTF8.GetByteCount(queued.Message));
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.StreamingProvider,
                Messages = CopyMessages(
                    committedMessages.Append(
                        new AgentChatMessage(
                            AgentChatMessageRole.User,
                            queued.Message))),
                ProvisionalAssistantText = string.Empty,
                ProvisionalReasoningSummary = string.Empty,
                QueuedFollowUpCount = _queuedFollowUps.Count,
                Status = "Preparing the queued follow-up…",
            };
            return FollowUpTurnTransition.Start(queued);
        }
    }

    private IReadOnlyList<GovernedAgentFollowUp>
        CaptureRecoverableFollowUpsUnsafe()
    {
        var recoverable = new List<GovernedAgentFollowUp>(
            _queuedFollowUps.Count + 1);
        if (_activeFollowUp is not null && !_activeFollowUpPromptCommitted)
        {
            recoverable.Add(_activeFollowUp);
        }

        recoverable.AddRange(_queuedFollowUps);
        return Array.AsReadOnly(recoverable.ToArray());
    }

    private void ClearQueuedFollowUpsUnsafe()
    {
        _queuedFollowUps.Clear();
        _queuedFollowUpBytes = 0;
        if (!_disposed)
        {
            _snapshot = _snapshot with { QueuedFollowUpCount = 0 };
        }
    }

    private void DiscardFollowUpsUnsafe()
    {
        _activeFollowUp = null;
        _activeFollowUpPromptCommitted = false;
        ClearQueuedFollowUpsUnsafe();
    }

    private sealed record FollowUpTurnTransition(
        bool IsCompleted,
        GovernedAgentFollowUp? Next)
    {
        public static FollowUpTurnTransition Interrupted { get; } =
            new(false, null);

        public static FollowUpTurnTransition Completed { get; } =
            new(true, null);

        public static FollowUpTurnTransition Start(
            GovernedAgentFollowUp followUp) =>
            new(false, followUp);
    }
}
