using GhostShell.Application;

namespace GhostShell.Infrastructure;

public sealed class RecoveryCoordinator : IRecoveryCoordinator
{
    private readonly IRuntimeRecoveryStore _runtimeRecoveryStore;
    private readonly ApplicationStartupState _startupState;

    public RecoveryCoordinator(
        IRuntimeRecoveryStore runtimeRecoveryStore,
        ApplicationStartupState startupState)
    {
        ArgumentNullException.ThrowIfNull(runtimeRecoveryStore);
        ArgumentNullException.ThrowIfNull(startupState);
        _runtimeRecoveryStore = runtimeRecoveryStore;
        _startupState = startupState;
    }

    public async ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> ResolveAsync(
        RecoveryChoice choice,
        CancellationToken cancellationToken)
    {
        var interruptedRunId = _startupState.InterruptedRunId;
        if (string.IsNullOrWhiteSpace(interruptedRunId))
        {
            return ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>.Failure(
                new ApplicationRunError(
                    ApplicationRunErrorCode.RunMismatch,
                    "There is no interrupted application run to recover."));
        }

        switch (choice)
        {
            case RecoveryChoice.Restore:
                return await _runtimeRecoveryStore.LoadAsync(interruptedRunId, cancellationToken)
                    .ConfigureAwait(false);
            case RecoveryChoice.SafeMode:
                return ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>.Success([]);
            case RecoveryChoice.DiscardRuntimeState:
                var discarded = await _runtimeRecoveryStore.DiscardAsync(
                        interruptedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
                return discarded.IsSuccess
                    ? ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>.Success([])
                    : ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>.Failure(
                        discarded.Error!);
            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, null);
        }
    }
}
