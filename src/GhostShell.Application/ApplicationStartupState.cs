namespace GhostShell.Application;

public enum RecoveryChoice
{
    Restore,
    SafeMode,
    DiscardRuntimeState,
}

public enum RecoveryDecisionState
{
    NotRequired,
    Pending,
    Resolved,
}

public sealed class ApplicationStartupState
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the whole profile is ready — run marker, recovery,
    /// catalog, preferences — not merely when this state is filled in.
    /// Session restore resolves connections against the catalog, so a
    /// signal that fired before the catalog loaded restored workspaces
    /// whose every adapter was "unavailable". With keys sealed under the
    /// startup PIN all of this happens behind the lock screen, not before
    /// the window.
    /// </summary>
    public Task Initialized => _initialized.Task;

    /// <summary>Called once by startup after the last initialization step.</summary>
    public void MarkProfileInitialized() => _initialized.TrySetResult();

    public ApplicationRunStart? Run { get; private set; }

    public RecoveryDecisionState RecoveryState { get; private set; }

    public RecoveryChoice? Choice { get; private set; }

    public IReadOnlyList<RuntimeRecoverySnapshot> RestoredSnapshots { get; private set; } = [];

    public bool ShouldRestoreRuntimeState =>
        RecoveryState == RecoveryDecisionState.Resolved && Choice == RecoveryChoice.Restore;

    public string? InterruptedRunId
    {
        get
        {
            lock (_gate)
            {
                return Run is { RecoveryRequired: true }
                    && RecoveryState == RecoveryDecisionState.Pending
                    ? Run.PreviousState.RunId
                    : null;
            }
        }
    }

    public void Initialize(ApplicationRunStart run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.RecoveryRequired && string.IsNullOrWhiteSpace(run.PreviousState.RunId))
        {
            throw new ArgumentException(
                "A recovery-required startup must identify the interrupted run.",
                nameof(run));
        }

        lock (_gate)
        {
            if (Run is not null)
            {
                throw new InvalidOperationException("Application startup state is already initialized.");
            }

            Run = run;
            RecoveryState = run.RecoveryRequired
                ? RecoveryDecisionState.Pending
                : RecoveryDecisionState.NotRequired;
        }
    }

    public void ResolveRecovery(
        RecoveryChoice choice,
        IReadOnlyList<RuntimeRecoverySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        lock (_gate)
        {
            if (Run is null || !Run.RecoveryRequired)
            {
                throw new InvalidOperationException("This application run does not require recovery.");
            }

            if (RecoveryState != RecoveryDecisionState.Pending)
            {
                throw new InvalidOperationException("Recovery has already been resolved.");
            }

            if (snapshots.Any(snapshot => snapshot.RunId != Run.PreviousState.RunId))
            {
                throw new ArgumentException(
                    "Recovery snapshots must belong to the interrupted run.",
                    nameof(snapshots));
            }

            if (choice != RecoveryChoice.Restore && snapshots.Count != 0)
            {
                throw new ArgumentException(
                    "Safe mode and discard cannot apply runtime recovery snapshots.",
                    nameof(snapshots));
            }

            Choice = choice;
            RestoredSnapshots = snapshots.ToArray();
            RecoveryState = RecoveryDecisionState.Resolved;
        }
    }
}
