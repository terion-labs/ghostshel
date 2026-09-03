using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Rewrites terminal process plans so the host PTY starts the workspace
/// isolation runtime instead of the connection executable directly.
/// </summary>
internal interface IWorkspaceIsolationTerminalRuntime
{
}

internal sealed class WorkspaceIsolatedConnectionRuntime :
    IConnectionRuntime,
    IWorkspaceIsolationTerminalRuntime
{
    private readonly IConnectionRuntime _inner;
    private readonly IWorkspaceIsolationProvider _provider;
    private readonly WorkspaceIsolationBinding _binding;

    public WorkspaceIsolatedConnectionRuntime(
        IConnectionRuntime inner,
        IWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (RejectBeforeHostPlanning(profile) is { } rejected)
        {
            return rejected;
        }

        var result = await _inner.PlanOpenAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        return Rewrite(profile, result);
    }

    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (RejectBeforeHostPlanning(profile) is { } rejected)
        {
            return rejected;
        }

        var result = await _inner.PlanOpenAsync(
                profile,
                multiplexerSession,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return Rewrite(profile, result);
    }

    private static ConnectionRuntimeResult<ConnectionOpenPlan>? RejectBeforeHostPlanning(
        ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ConnectionKind is ConnectionKind.Docker or ConnectionKind.Wsl)
        {
            return Fail(WorkspaceIsolationError.Create(
                WorkspaceIsolationErrorCode.UnsupportedConnectionKind));
        }

        if (profile.Authentication is ConnectionAuthentication.Password
                or ConnectionAuthentication.PrivateKey
                or ConnectionAuthentication.SshAgent
            || profile.Startup.Environment.Any(variable =>
                variable.Value is ConnectionEnvironmentValue.Secret))
        {
            return Fail(WorkspaceIsolationError.Create(
                WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable));
        }

        // A host-side key scan would escape the workspace's future network boundary,
        // while guest OpenSSH accept-new would create a second, unreviewed trust store.
        // Until inspection and approval run inside the isolate, only the user's explicit
        // verification-disabled policy can cross this boundary.
        if (profile.ConnectionKind == ConnectionKind.Ssh
            && profile.HostKeyPolicy != SshHostKeyPolicy.InsecureIgnore)
        {
            return Fail(WorkspaceIsolationError.Create(
                WorkspaceIsolationErrorCode.SshHostKeyTrustUnavailable));
        }

        return null;
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        _ = profile;
        _ = progress;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Fail(
            new ConnectionRuntimeError(
                ConnectionRuntimeErrorCode.UnsupportedPlatform,
                "workspace_isolation_test_unavailable",
                "Connection tests are unavailable until they can run inside the workspace isolate.",
                Retryable: false,
                ConnectionRecoveryAction.None)));
    }

    private ConnectionRuntimeResult<ConnectionOpenPlan> Rewrite(
        ConnectionProfile profile,
        ConnectionRuntimeResult<ConnectionOpenPlan> result)
    {
        if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure)
        {
            return result;
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)result).Value;
        if (string.IsNullOrWhiteSpace(plan.Launch.Executable))
        {
            return Fail(WorkspaceIsolationError.Create(
                WorkspaceIsolationErrorCode.ExecutableMappingUnavailable));
        }

        var isolated = _provider.CreateExecLaunch(
            _binding,
            new WorkspaceIsolationProcessRequest(
                plan.Kind,
                plan.Launch.Executable,
                plan.Launch.Arguments,
                plan.Launch.Environment,
                plan.Kind == ConnectionKind.Local && profile.Startup.Directory is null
                    ? null
                    : plan.Launch.WorkingDirectory,
                WorkspaceProcessMode.Interactive | WorkspaceProcessMode.AllocateTerminal,
                usesHostCredentialBroker: plan.IsSecretBrokerPrepared));
        if (isolated is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure failure)
        {
            return Fail(failure.Error);
        }

        var process = ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)isolated).Value;
        var launch = new TerminalLaunchRequest(
            process.HostWorkingDirectory,
            process.Executable,
            process.Arguments,
            process.Environment,
            plan.Launch.RenderProfile,
            plan.Launch.Keymap,
            plan.Launch.ConnectionId,
            plan.Launch.ConnectionMetadata,
            plan.Launch.InitialCommand,
            plan.Launch.ShellActivityFallback,
            plan.Launch.MultiplexerSession);
        return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
            new ConnectionOpenPlan(
                plan.ConnectionId,
                plan.Kind,
                launch,
                plan.Authentication,
                plan.HostKeyPolicy,
                plan.ReconnectMode,
                plan.SecretRequirements,
                plan.Warnings,
                plan.IsSecretBrokerPrepared));
    }

    private static ConnectionRuntimeResult<ConnectionOpenPlan> Fail(
        WorkspaceIsolationError error) =>
        ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(new ConnectionRuntimeError(
            MapErrorCode(error.Code),
            error.StableCode,
            error.Message,
            error.Retryable,
            MapRecoveryAction(error.RecoveryAction)));

    private static ConnectionRuntimeErrorCode MapErrorCode(
        WorkspaceIsolationErrorCode code) => code switch
        {
            WorkspaceIsolationErrorCode.RuntimeMissing
                or WorkspaceIsolationErrorCode.RuntimeVersionTooOld =>
                ConnectionRuntimeErrorCode.RuntimeMissing,
            WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted =>
                ConnectionRuntimeErrorCode.InvalidProfile,
            WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired =>
                ConnectionRuntimeErrorCode.InvalidProfile,
            WorkspaceIsolationErrorCode.UnsupportedConnectionKind
                or WorkspaceIsolationErrorCode.ExecutableMappingUnavailable
                or WorkspaceIsolationErrorCode.SshHostKeyTrustUnavailable =>
                ConnectionRuntimeErrorCode.UnsupportedPlatform,
            WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable =>
                ConnectionRuntimeErrorCode.AuthenticationRequired,
            WorkspaceIsolationErrorCode.Cancelled => ConnectionRuntimeErrorCode.Cancelled,
            WorkspaceIsolationErrorCode.Timeout => ConnectionRuntimeErrorCode.Timeout,
            WorkspaceIsolationErrorCode.RuntimeUnavailable
                or WorkspaceIsolationErrorCode.PrepareFailed
                or WorkspaceIsolationErrorCode.StopFailed =>
                ConnectionRuntimeErrorCode.ProcessFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };

    private static ConnectionRecoveryAction MapRecoveryAction(
        WorkspaceIsolationRecoveryAction action) => action switch
        {
            WorkspaceIsolationRecoveryAction.InstallRuntime
                or WorkspaceIsolationRecoveryAction.UpdateRuntime =>
                ConnectionRecoveryAction.InstallRuntime,
            WorkspaceIsolationRecoveryAction.StartRuntime
                or WorkspaceIsolationRecoveryAction.Retry =>
                ConnectionRecoveryAction.Retry,
            WorkspaceIsolationRecoveryAction.ChooseMountedDirectory =>
                ConnectionRecoveryAction.EditProfile,
            WorkspaceIsolationRecoveryAction.ResetPersistentEnvironment =>
                ConnectionRecoveryAction.EditProfile,
            WorkspaceIsolationRecoveryAction.None
                or WorkspaceIsolationRecoveryAction.DisableIsolation =>
                ConnectionRecoveryAction.None,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
}

internal sealed class WorkspaceIsolationUnavailableConnectionRuntime :
    IConnectionRuntime,
    IWorkspaceIsolationTerminalRuntime
{
    public static WorkspaceIsolationUnavailableConnectionRuntime ScopeChanged { get; } = new(
        "workspace_isolation_scope_changed",
        "Workspace isolation changed while this workspace was open. Close and reopen it before starting another terminal.");

    public static WorkspaceIsolationUnavailableConnectionRuntime BindingMissing { get; } = new(
        "workspace_isolation_binding_missing",
        "The workspace isolation binding is unavailable. Close and reopen the workspace before starting another terminal.");

    private readonly ConnectionRuntimeError _error;

    private WorkspaceIsolationUnavailableConnectionRuntime(string stableCode, string message)
    {
        _error = new ConnectionRuntimeError(
            ConnectionRuntimeErrorCode.ProcessFailed,
            stableCode,
            message,
            Retryable: false,
            ConnectionRecoveryAction.None);
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(_error));
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken) =>
        PlanOpenAsync(profile, progress, cancellationToken);

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Fail(_error));
    }
}
