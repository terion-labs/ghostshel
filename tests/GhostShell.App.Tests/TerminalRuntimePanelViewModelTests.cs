using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class TerminalRuntimePanelViewModelTests
{
    [Fact]
    public async Task SecretBrokerPlanNeverBecomesAnExecutableSessionRequest()
    {
        var connection = SshPasswordConnection();
        var launch = new TerminalLaunchRequest(null, "/usr/bin/ssh", ["host"]);
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Ssh,
                launch,
                ConnectionAuthenticationMode.Password,
                SshHostKeyPolicy.Strict,
                ConnectionReconnectMode.Manual,
                [new ConnectionSecretRequirement(
                    ConnectionSecretRole.Password,
                    new SecretRef("password-secret"))])));
        using var panel = CreatePanel(runtime, connection, PanelStartupBehavior.None);

        await panel.Initialization;

        Assert.Equal(ConnectionPanelState.CredentialBrokerRequired, panel.ConnectionState);
        Assert.Null(panel.SessionRequest);
        Assert.True(panel.HasConnectionOverlay);
    }

    [Fact]
    public async Task SuccessfulPlanAppliesPanelStartupRenderProfileAndKeymapSnapshot()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(profile =>
        {
            Assert.Equal("/panel/work", profile.Startup.Directory);
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                profile.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(profile.Startup.Directory, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable));
        });
        var startup = new PanelStartupBehavior("/panel/work", ["git status", "pwd"]);
        var render = TerminalRenderProfileSnapshot.FromProfile(DefaultTerminalProfile());
        var keymap = TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.LinuxTerminal);
        using var panel = CreatePanel(runtime, connection, startup, render, keymap: keymap);

        await panel.Initialization;

        Assert.Equal(ConnectionPanelState.Ready, panel.ConnectionState);
        Assert.NotNull(panel.SessionRequest);
        Assert.Same(render, panel.SessionRequest.Launch.RenderProfile);
        Assert.Same(keymap, panel.SessionRequest.Launch.Keymap);
        Assert.Equal("/panel/work", panel.SessionRequest.Launch.WorkingDirectory);
        Assert.Equal(["git status", "pwd"], panel.StartupCommands);
        Assert.Equal(panel.Id, panel.StartupCommandDispatchState.PanelId);
        Assert.Same(
            panel.StartupCommandContext,
            panel.StartupCommandDispatchState.Context);
        Assert.Equal(
            ["git status", "pwd"],
            panel.StartupCommandDispatchState.Commands);
    }

    [Fact]
    public async Task AgentLivenessRequiresAnObservedExactActiveHostSession()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    connection.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(null, "/bin/zsh"),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(
            runtime,
            connection,
            PanelStartupBehavior.None);

        await panel.Initialization;

        var request = Assert.IsType<EnsureTerminalSessionRequest>(
            panel.SessionRequest);
        Assert.False(panel.HasObservedActiveSession);

        panel.ObserveSessionSnapshot(
            Snapshot(
                request,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                request.Owner with
                {
                    PanelId = PanelInstanceId.New(),
                }));

        Assert.False(panel.HasObservedActiveSession);

        panel.ObserveSessionSnapshot(
            Snapshot(
                request,
                SessionLifecycle.Active,
                SessionHealth.Healthy));

        Assert.True(panel.HasObservedActiveSession);

        panel.ObserveSessionSnapshot(
            Snapshot(
                request,
                SessionLifecycle.Closed,
                SessionHealth.Ended));

        Assert.False(panel.HasObservedActiveSession);
        Assert.Null(panel.SessionRequest);
    }

    [Fact]
    public async Task RuntimeMissingCanRetryAfterExternalRepair()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.RuntimeMissing)),
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(runtime, connection, PanelStartupBehavior.None);

        await panel.Initialization;
        Assert.Equal(ConnectionPanelState.Failed, panel.ConnectionState);
        Assert.True(panel.CanRetry);

        await panel.RetryAsync();

        Assert.Equal(ConnectionPanelState.Ready, panel.ConnectionState);
        Assert.NotNull(panel.SessionRequest);
        Assert.Equal(2, runtime.PlanCount);
    }

    [Fact]
    public async Task RetryableRemoteFailureUsesBoundedReconnectStateBeforeStartingSession()
    {
        var connection = SshAgentConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Offline)),
            SuccessfulSshPlan(connection));
        using var panel = CreatePanel(
            runtime,
            connection,
            PanelStartupBehavior.None,
            reconnectDelay: (_, _) => Task.CompletedTask);

        await panel.Initialization;

        Assert.Equal(2, runtime.PlanCount);
        Assert.Equal(1, panel.ReconnectAttempt);
        Assert.Equal(ConnectionReconnectState.WaitingForSession, panel.ReconnectState);
        Assert.NotNull(panel.SessionRequest);
    }

    [Fact]
    public async Task WaitingReconnectCanBeCancelledWithoutAnotherAttempt()
    {
        var connection = SshAgentConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Offline)));
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var panel = CreatePanel(
            runtime,
            connection,
            PanelStartupBehavior.None,
            reconnectDelay: async (_, cancellationToken) =>
            {
                delayStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await delayStarted.Task;

        panel.CancelReconnect();
        await panel.Initialization;

        Assert.Equal(1, runtime.PlanCount);
        Assert.Equal(ConnectionReconnectState.Cancelled, panel.ReconnectState);
        Assert.Equal(ConnectionPanelState.Failed, panel.ConnectionState);
        Assert.True(panel.CanRetry);
    }

    [Fact]
    public async Task RetryableSessionFailureCreatesANewSessionRequest()
    {
        var connection = SshAgentConnection();
        var runtime = new QueueConnectionRuntime(
            SuccessfulSshPlan(connection),
            SuccessfulSshPlan(connection));
        var keymap = TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.LinuxTerminal);
        using var panel = CreatePanel(
            runtime,
            connection,
            PanelStartupBehavior.None,
            reconnectDelay: (_, _) => Task.CompletedTask,
            keymap: keymap);
        await panel.Initialization;
        var first = panel.SessionRequest!;
        var startupBatchContext = panel.StartupCommandContext;
        panel.ObserveSessionSnapshot(new SessionSnapshot(
            new SessionDescriptor(
                first.SessionId,
                PanelKind.Terminal,
                SessionLifecycle.Failed,
                SessionHealth.Failed,
                first.Owner,
                CapabilitySet.Empty,
                Revision: 2,
                HasActiveWork: false,
                StatusDetail: "Connection lost.",
                Failure: new SessionFailure("terminal_connection_lost", "Connection lost.", Retryable: true)),
            LastSequence: 2,
            Attachments: [],
            InputLease: null));

        await panel.Initialization;

        Assert.Equal(2, runtime.PlanCount);
        Assert.NotEqual(first.SessionId, panel.SessionRequest!.SessionId);
        Assert.Same(keymap, first.Launch.Keymap);
        Assert.Same(keymap, panel.SessionRequest.Launch.Keymap);
        Assert.Same(startupBatchContext, panel.StartupCommandContext);
        Assert.NotNull(panel.StartupCommandContext.IdempotencyKey);
        Assert.Equal(ConnectionReconnectState.WaitingForSession, panel.ReconnectState);
    }

    [Fact]
    public async Task StartupCommandsBecomeOneShotAfterTheLiveHostConfirmsDelivery()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(
            runtime,
            connection,
            new PanelStartupBehavior(null, ["deploy"]));
        await panel.Initialization;

        panel.ObserveStartupCommandDispatch(TerminalStartupCommandDispatchResult.Success());

        Assert.Empty(panel.StartupCommands);
    }

    [Fact]
    public async Task FailedStartupCommandDeliveryRemainsVisibleAndRetryable()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(
            runtime,
            connection,
            new PanelStartupBehavior(null, ["deploy"]));
        await panel.Initialization;

        panel.ObserveStartupCommandDispatch(TerminalStartupCommandDispatchResult.Failure(
            new TerminalStartupCommandDispatchError(
                TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
                "Delivery acknowledgement was lost.",
                Retryable: true)));

        Assert.Equal(["deploy"], panel.StartupCommands);
        Assert.True(panel.HasStartupCommandError);
        Assert.Contains("Retrying", panel.StartupCommandErrorDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopPolicyLatchesTheFirstTypedFailureAboveRendererRecreation(
        bool retryable)
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(
            runtime,
            connection,
            new PanelStartupBehavior(
                commands: ["deploy"],
                deliveryFailurePolicy:
                    StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure));
        await panel.Initialization;
        var request = panel.SessionRequest;
        var error = new TerminalStartupCommandDispatchError(
            TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
            "Delivery acknowledgement was lost.",
            retryable);

        panel.ObserveStartupCommandDispatch(
            TerminalStartupCommandDispatchResult.Failure(error));

        Assert.Empty(panel.StartupCommands);
        Assert.Same(request, panel.SessionRequest);
        Assert.Same(error, panel.StartupCommandError);
        Assert.True(panel.HasStartupCommandError);
        Assert.DoesNotContain("Retrying", panel.StartupCommandErrorDetail, StringComparison.Ordinal);
        Assert.Contains("will not be retried", panel.StartupCommandErrorDetail, StringComparison.Ordinal);
        Assert.Contains("terminal remains open", panel.StartupCommandErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopPolicyLatchAndTypedErrorSurviveAutomaticReconnect()
    {
        var connection = SshAgentConnection();
        var runtime = new QueueConnectionRuntime(
            SuccessfulSshPlan(connection),
            SuccessfulSshPlan(connection));
        using var panel = CreatePanel(
            runtime,
            connection,
            new PanelStartupBehavior(
                commands: ["deploy"],
                deliveryFailurePolicy:
                    StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure),
            reconnectDelay: (_, _) => Task.CompletedTask);
        await panel.Initialization;
        var first = panel.SessionRequest!;
        var dispatchState = panel.StartupCommandDispatchState;
        var error = new TerminalStartupCommandDispatchError(
            TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
            "Delivery acknowledgement was lost.",
            Retryable: true);
        var dispatchResult = await dispatchState.DispatchIfNeededAsync(
            panel.Id,
            (_, _) => ValueTask.FromResult(
                TerminalStartupCommandDispatchResult.Failure(error)),
            CancellationToken.None);
        Assert.Same(error, panel.StartupCommandError);
        Assert.Empty(panel.StartupCommands);

        panel.ObserveSessionSnapshot(new SessionSnapshot(
            new SessionDescriptor(
                first.SessionId,
                PanelKind.Terminal,
                SessionLifecycle.Failed,
                SessionHealth.Failed,
                first.Owner,
                CapabilitySet.Empty,
                Revision: 2,
                HasActiveWork: false,
                StatusDetail: "Connection lost.",
                Failure: new SessionFailure(
                    "terminal_connection_lost",
                    "Connection lost.",
                    Retryable: true)),
            LastSequence: 2,
            Attachments: [],
            InputLease: null));
        await panel.Initialization;

        Assert.NotEqual(first.SessionId, panel.SessionRequest!.SessionId);
        Assert.Same(dispatchState, panel.StartupCommandDispatchState);
        Assert.Same(dispatchResult, dispatchState.LastResult);
        Assert.Empty(panel.StartupCommands);
        Assert.Same(error, panel.StartupCommandError);
        Assert.Contains("will not be retried", panel.StartupCommandErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePanelPinsTheDeliveryPolicyFromItsDefinitionInstance()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        var definitionStartup = new PanelStartupBehavior(
            commands: ["deploy"],
            deliveryFailurePolicy:
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        using var panel = CreatePanel(runtime, connection, definitionStartup);
        await panel.Initialization;

        definitionStartup = new PanelStartupBehavior(
            commands: ["deploy"],
            deliveryFailurePolicy:
                StartupCommandDeliveryFailurePolicy.RetryWhileLive);

        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            panel.StartupCommandDeliveryFailurePolicy);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.RetryWhileLive,
            definitionStartup.DeliveryFailurePolicy);
    }

    [Fact]
    public async Task RendererOutcomeOnlyLatchesTheExactDefinitionInstanceBatch()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(
            runtime,
            connection,
            new PanelStartupBehavior(
                commands: ["deploy"],
                deliveryFailurePolicy:
                    StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure));
        await panel.Initialization;
        var failure = TerminalStartupCommandDispatchResult.Failure(
            new TerminalStartupCommandDispatchError(
                TerminalStartupCommandDispatchErrorCode.Cancelled,
                "The renderer attachment changed.",
                Retryable: true));

        panel.ObserveStartupCommandDispatch(
            OperationContext.ForHuman(
                panel.ClientId,
                idempotencyKey: IdempotencyKey.New()),
            failure);

        Assert.Equal(["deploy"], panel.StartupCommands);
        Assert.Null(panel.StartupCommandError);

        panel.ObserveStartupCommandDispatch(
            panel.StartupCommandContext,
            failure);

        Assert.Empty(panel.StartupCommands);
        Assert.Equal(failure.Error, panel.StartupCommandError);
    }

    [Fact]
    public async Task DeliveredCommandsAreNeverReplayedWhenCompletionAuditFails()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(
            runtime,
            connection,
            new PanelStartupBehavior(null, ["deploy"]));
        await panel.Initialization;

        panel.ObserveStartupCommandDispatch(TerminalStartupCommandDispatchResult.Failure(
            new TerminalStartupCommandDispatchError(
                TerminalStartupCommandDispatchErrorCode.AuditPersistenceFailure,
                "The outcome audit failed.",
                Retryable: false),
            commandsDelivered: true));

        Assert.Empty(panel.StartupCommands);
        Assert.True(panel.HasStartupCommandError);
        Assert.DoesNotContain("Retrying", panel.StartupCommandErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrictUnknownHostKeyBlocksTheSessionWithReviewMetadata()
    {
        var connection = SshAgentConnection();
        var runtime = new QueueConnectionRuntime(SuccessfulSshPlan(connection));
        var security = new FixedConnectionSecurityRuntime(connection, SshHostKeyDisposition.Unknown);
        using var panel = CreatePanel(
            runtime,
            connection,
            PanelStartupBehavior.None,
            security: security);

        await panel.Initialization;

        Assert.Equal(ConnectionPanelState.Failed, panel.ConnectionState);
        Assert.Equal(ConnectionRuntimeErrorCode.UnknownHostKey, panel.ConnectionError!.Code);
        Assert.Equal(SshHostKeyDisposition.Unknown, panel.HostKeyReview!.Disposition);
        Assert.Equal(0, runtime.PlanCount);
    }

    [Fact]
    public async Task AcceptNewPolicyAtomicallyTrustsTheFirstKeyButNeverFlattensChanged()
    {
        var connection = SshAgentConnection(SshHostKeyPolicy.AcceptNew);
        var runtime = new QueueConnectionRuntime(SuccessfulSshPlan(connection));
        var security = new FixedConnectionSecurityRuntime(connection, SshHostKeyDisposition.Unknown);
        using var panel = CreatePanel(
            runtime,
            connection,
            PanelStartupBehavior.None,
            security: security);

        await panel.Initialization;

        Assert.Equal(1, security.TrustCount);
        Assert.Equal(ConnectionPanelState.Ready, panel.ConnectionState);
        Assert.NotNull(panel.SessionRequest);
    }

    [Fact]
    public async Task AcceptNewPolicyStillBlocksAChangedKeyForExplicitReview()
    {
        var connection = SshAgentConnection(SshHostKeyPolicy.AcceptNew);
        var runtime = new QueueConnectionRuntime(SuccessfulSshPlan(connection));
        var security = new FixedConnectionSecurityRuntime(connection, SshHostKeyDisposition.Changed);
        using var panel = CreatePanel(
            runtime,
            connection,
            PanelStartupBehavior.None,
            security: security);

        await panel.Initialization;

        Assert.Equal(0, security.TrustCount);
        Assert.Equal(ConnectionRuntimeErrorCode.HostKeyChanged, panel.ConnectionError!.Code);
        Assert.True(panel.HostKeyReview!.RequiresExplicitReplacement);
        Assert.Null(panel.SessionRequest);
    }

    [Fact]
    public async Task CopyModeIsExplicitAndCanBeExitedWithoutChangingTheSession()
    {
        var connection = LocalConnection();
        var runtime = new QueueConnectionRuntime(
            ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
                connection.Id,
                ConnectionKind.Local,
                new TerminalLaunchRequest(null, "/bin/zsh"),
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable)));
        using var panel = CreatePanel(runtime, connection, PanelStartupBehavior.None);
        await panel.Initialization;
        var sessionRequest = panel.SessionRequest;

        Assert.True(panel.EnterCopyMode());
        Assert.True(panel.IsCopyMode);
        Assert.False(panel.EnterCopyMode());
        Assert.Same(sessionRequest, panel.SessionRequest);

        Assert.True(panel.ExitCopyMode());
        Assert.False(panel.IsCopyMode);
        Assert.False(panel.ExitCopyMode());
        Assert.Same(sessionRequest, panel.SessionRequest);
    }

    private static SessionSnapshot Snapshot(
        EnsureTerminalSessionRequest request,
        SessionLifecycle lifecycle,
        SessionHealth health,
        SessionOwner? owner = null) =>
        new(
            new SessionDescriptor(
                request.SessionId,
                PanelKind.Terminal,
                lifecycle,
                health,
                owner ?? request.Owner,
                CapabilitySet.Empty,
                Revision: 2,
                HasActiveWork: false,
                StatusDetail: lifecycle.ToString()),
            LastSequence: 2,
            Attachments: [],
            InputLease: null);

    private static TerminalRuntimePanelViewModel CreatePanel(
        IConnectionRuntime runtime,
        ConnectionProfile connection,
        PanelStartupBehavior startup,
        TerminalRenderProfileSnapshot? render = null,
        IConnectionSecurityRuntime? security = null,
        Func<TimeSpan, CancellationToken, Task>? reconnectDelay = null,
        TerminalKeymapSnapshot? keymap = null)
    {
        var panelId = PanelInstanceId.New();
        return new TerminalRuntimePanelViewModel(
            panelId,
            connection.Name,
            runtime,
            connection,
            new SessionOwner(
                HostMode.Desktop,
                WindowInstanceId.New(),
                WorkspaceInstanceId.New(),
                TabInstanceId.New(),
                panelId),
            startup,
            render,
            DispatchProxy.Create<ISessionHostClient, NullSessionHostClientProxy>(),
            ClientId.New(),
            new TerminalStartupCommandDispatcher(new SuccessfulAuditStore(), TimeProvider.System),
            security,
            reconnectDelay: reconnectDelay,
            keymap: keymap);
    }

    private static ConnectionProfile LocalConnection() => new(
        new ConnectionId("local-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "Local",
        new ConnectionEndpoint.Local("/bin/zsh"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private static ConnectionProfile SshPasswordConnection() => new(
        new ConnectionId("ssh-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "SSH",
        new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
        new ConnectionAuthentication.Password(new SecretRef("password-secret")),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.Strict);

    private static ConnectionProfile SshAgentConnection(
        SshHostKeyPolicy hostKeyPolicy = SshHostKeyPolicy.Strict) => new(
        new ConnectionId("ssh-agent-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "SSH agent",
        new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
        new ConnectionAuthentication.SshAgent(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        hostKeyPolicy);

    private static ConnectionRuntimeResult<ConnectionOpenPlan> SuccessfulSshPlan(
        ConnectionProfile connection) =>
        ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(new ConnectionOpenPlan(
            connection.Id,
            ConnectionKind.Ssh,
            new TerminalLaunchRequest(null, "/usr/bin/ssh", ["host.example"]),
            ConnectionAuthenticationMode.SshAgent,
            connection.HostKeyPolicy,
            ConnectionReconnectMode.BoundedBackoff));

    private static TerminalProfile DefaultTerminalProfile() => new(
        new TerminalProfileId("terminal-test"),
        "Terminal",
        "JetBrains Mono",
        14,
        1.4,
        TerminalCursorStyle.Block,
        true,
        100_000,
        TerminalPalette.GhostShellDark,
        new KeymapProfileId("keymap-test"));

    public class NullSessionHostClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class SuccessfulAuditStore : IAuditStore
    {
        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success([]));
    }

    private sealed class QueueConnectionRuntime : IConnectionRuntime
    {
        private readonly Queue<Func<ConnectionProfile, ConnectionRuntimeResult<ConnectionOpenPlan>>> _plans;

        public QueueConnectionRuntime(params ConnectionRuntimeResult<ConnectionOpenPlan>[] plans)
            : this(plans.Select(result =>
                new Func<ConnectionProfile, ConnectionRuntimeResult<ConnectionOpenPlan>>(_ => result)).ToArray())
        {
        }

        public QueueConnectionRuntime(
            params Func<ConnectionProfile, ConnectionRuntimeResult<ConnectionOpenPlan>>[] plans)
        {
            _plans = new Queue<Func<ConnectionProfile, ConnectionRuntimeResult<ConnectionOpenPlan>>>(plans);
        }

        public int PlanCount { get; private set; }

        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanCount++;
            return ValueTask.FromResult(_plans.Dequeue()(profile));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedConnectionSecurityRuntime(
        ConnectionProfile profile,
        SshHostKeyDisposition disposition) : IConnectionSecurityRuntime
    {
        public int TrustCount { get; private set; }

        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
            ConnectionProfile inspectedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(profile.Id, inspectedProfile.Id);
            var presented = Identity('A');
            return ValueTask.FromResult(ConnectionRuntimeResult<SshHostKeyReview>.Succeed(
                new SshHostKeyReview(
                    SshHostKeyReviewId.New(),
                    profile.Id,
                    "host.example:22",
                    disposition,
                    presented,
                    disposition is SshHostKeyDisposition.Trusted or SshHostKeyDisposition.Changed
                        ? Identity(disposition == SshHostKeyDisposition.Trusted ? 'A' : 'B')
                        : null,
                    DateTimeOffset.UtcNow.AddMinutes(5))));
        }

        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
            SshHostKeyTrustRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrustCount++;
            var identity = Identity('A');
            return ValueTask.FromResult(ConnectionRuntimeResult<SshHostKeyReview>.Succeed(
                new SshHostKeyReview(
                    SshHostKeyReviewId.New(),
                    request.ConnectionId,
                    "host.example:22",
                    SshHostKeyDisposition.Trusted,
                    identity,
                    identity,
                    DateTimeOffset.UtcNow.AddMinutes(5))));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionDiagnosticsReport>> DiagnoseAsync(
            ConnectionProfile diagnosedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private static SshHostKeyIdentity Identity(char marker) =>
            new("ssh-ed25519", $"SHA256:{new string(marker, 43)}");
    }
}
