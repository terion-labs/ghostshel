namespace GhostShell.Application;

public interface IRecoveryCoordinator
{
    ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> ResolveAsync(
        RecoveryChoice choice,
        CancellationToken cancellationToken);
}
