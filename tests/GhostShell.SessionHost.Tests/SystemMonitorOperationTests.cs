using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Protocol;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class SystemMonitorOperationTests
{
    private static readonly WindowInstanceId WindowId = new("monitor-window");
    private static readonly WorkspaceInstanceId WorkspaceId = new("monitor-workspace");
    private static readonly TabInstanceId TabId = new("monitor-tab");
    private static readonly PanelInstanceId StatisticsPanelId = new("statistics-panel");
    private static readonly PanelInstanceId ProcessPanelId = new("process-panel");
    private static readonly SessionId StatisticsSessionId = new("statistics-session");
    private static readonly SessionId ProcessSessionId = new("process-session");

    [Fact]
    public async Task NegotiationAndMissingFactoryReflectConfiguredCapabilities()
    {
        var monitorFactory = new FakeSystemMonitorPanelSessionFactory();
        await using var configured = CreateHost(monitorFactory);
        await using var missing = CreateHost(null);
        var hello = (await configured.NegotiateAsync(
            new ClientHello([ProtocolVersions.Current], MonitorCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var missingHello = (await missing.NegotiateAsync(
            new ClientHello([ProtocolVersions.Current], MonitorCapabilities()),
            Context(),
            CancellationToken.None)).Value();

        Assert.True(hello.Capabilities.Contains(SessionCapabilities.StatisticsRead));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.ProcessesList));
        Assert.False(missingHello.Capabilities.Contains(SessionCapabilities.StatisticsRead));
        Assert.False(missingHello.Capabilities.Contains(SessionCapabilities.ProcessesList));

        var statistics = await missing.EnsureStatisticsSessionAsync(
            StatisticsRequest(),
            Context(),
            CancellationToken.None);
        var processes = await missing.EnsureProcessMonitorSessionAsync(
            ProcessRequest(),
            Context(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.CapabilityNotSupported, statistics.Error().Code);
        Assert.Equal(HostErrorCode.CapabilityNotSupported, processes.Error().Code);
    }

    [Fact]
    public async Task EnsureLinksMatchingWorkspacePanelsAndRejectsKindMismatch()
    {
        var monitorFactory = new FakeSystemMonitorPanelSessionFactory();
        await using var host = CreateHost(monitorFactory);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();

        var mismatch = await host.EnsureProcessMonitorSessionAsync(
            new EnsureProcessMonitorSessionRequest(
                new SessionId("mismatched-session"),
                Owner(StatisticsPanelId),
                "Wrong kind"),
            Context(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.InvalidRequest, mismatch.Error().Code);
        Assert.Equal(0, monitorFactory.ProcessMonitorCreateCount);

        var statistics = (await host.EnsureStatisticsSessionAsync(
            StatisticsRequest(),
            Context(),
            CancellationToken.None)).Value();
        var processes = (await host.EnsureProcessMonitorSessionAsync(
            ProcessRequest(),
            Context(),
            CancellationToken.None)).Value();
        var linked = (await host.GetWorkspaceGraphAsync(
            WorkspaceId,
            Context(),
            CancellationToken.None)).Value();
        var panels = Assert.Single(linked.Workspace.Tabs).Panels;

        Assert.Equal(PanelKind.Statistics, statistics.Descriptor.Kind);
        Assert.Equal(Owner(StatisticsPanelId), statistics.Descriptor.Owner);
        Assert.Equal(PanelKind.ProcessMonitor, processes.Descriptor.Kind);
        Assert.Equal(Owner(ProcessPanelId), processes.Descriptor.Owner);
        Assert.Equal(
            StatisticsSessionId,
            panels.Single(panel => panel.Id == StatisticsPanelId).SessionId);
        Assert.Equal(
            ProcessSessionId,
            panels.Single(panel => panel.Id == ProcessPanelId).SessionId);
        Assert.Equal(1, monitorFactory.StatisticsCreateCount);
        Assert.Equal(1, monitorFactory.ProcessMonitorCreateCount);
    }

    [Fact]
    public async Task EnsureReplaysIdempotentlyAndRejectsSessionIdCollisions()
    {
        var monitorFactory = new FakeSystemMonitorPanelSessionFactory();
        await using var host = CreateHost(monitorFactory);
        var context = Context(idempotencyKey: new IdempotencyKey("monitor-open-once"));

        var first = (await host.EnsureStatisticsSessionAsync(
            StatisticsRequest(),
            context,
            CancellationToken.None)).Value();
        var replay = (await host.EnsureStatisticsSessionAsync(
            StatisticsRequest(),
            context,
            CancellationToken.None)).Value();
        var reused = await host.EnsureProcessMonitorSessionAsync(
            ProcessRequest(),
            context,
            CancellationToken.None);
        var collision = await host.EnsureProcessMonitorSessionAsync(
            new EnsureProcessMonitorSessionRequest(
                StatisticsSessionId,
                Owner(ProcessPanelId),
                "Colliding process monitor"),
            Context(),
            CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal(1, monitorFactory.StatisticsCreateCount);
        Assert.Equal(HostErrorCode.IdempotencyKeyReused, reused.Error().Code);
        Assert.Equal(HostErrorCode.InvalidRequest, collision.Error().Code);
        Assert.Equal(0, monitorFactory.ProcessMonitorCreateCount);
    }

    [Fact]
    public async Task WrongKindOperationsAreRejectedBeforeMonitorDispatch()
    {
        var monitorFactory = new FakeSystemMonitorPanelSessionFactory();
        await using var host = CreateHost(monitorFactory);
        _ = (await host.EnsureStatisticsSessionAsync(
            StatisticsRequest(),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.EnsureProcessMonitorSessionAsync(
            ProcessRequest(),
            Context(),
            CancellationToken.None)).Value();

        var listStatistics = await host.ListProcessesAsync(
            new ProcessMonitorHostRequest(
                StatisticsSessionId,
                new ProcessMonitorQuery()),
            Context(),
            CancellationToken.None);
        var readProcesses = await host.ReadStatisticsAsync(
            ProcessSessionId,
            Context(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.CapabilityNotSupported, listStatistics.Error().Code);
        Assert.Equal(HostErrorCode.CapabilityNotSupported, readProcesses.Error().Code);
        Assert.Equal(0, monitorFactory.Statistics(StatisticsSessionId).ReadCount);
        Assert.Equal(0, monitorFactory.Processes(ProcessSessionId).ListCount);
    }

    [Fact]
    public async Task ClosedMonitorSessionsRejectReadsAndDisposeWithTheHost()
    {
        var monitorFactory = new FakeSystemMonitorPanelSessionFactory();
        var host = CreateHost(monitorFactory);
        try
        {
            _ = (await host.EnsureStatisticsSessionAsync(
                StatisticsRequest(),
                Context(),
                CancellationToken.None)).Value();
            _ = (await host.EnsureProcessMonitorSessionAsync(
                ProcessRequest(),
                Context(),
                CancellationToken.None)).Value();

            var statisticsClose = (await host.CloseAsync(
                CloseScopeRequest.Session(
                    StatisticsSessionId,
                    CloseDecision.Request),
                Context(),
                CancellationToken.None)).Value();
            var processClose = (await host.CloseAsync(
                CloseScopeRequest.Session(
                    ProcessSessionId,
                    CloseDecision.Request),
                Context(),
                CancellationToken.None)).Value();

            var statisticsCompleted =
                Assert.IsType<CloseScopeResult.Completed>(statisticsClose);
            var processCompleted =
                Assert.IsType<CloseScopeResult.Completed>(processClose);
            Assert.Equal(
                SessionCloseOutcome.GracefullyClosed,
                Assert.Single(statisticsCompleted.Sessions).Outcome);
            Assert.Equal(
                SessionCloseOutcome.GracefullyClosed,
                Assert.Single(processCompleted.Sessions).Outcome);
            Assert.Equal(
                PanelCloseMode.Graceful,
                monitorFactory.Statistics(StatisticsSessionId).LastCloseMode);
            Assert.Equal(
                PanelCloseMode.Graceful,
                monitorFactory.Processes(ProcessSessionId).LastCloseMode);

            var statisticsRead = await host.ReadStatisticsAsync(
                StatisticsSessionId,
                Context(),
                CancellationToken.None);
            var processList = await host.ListProcessesAsync(
                new ProcessMonitorHostRequest(
                    ProcessSessionId,
                    new ProcessMonitorQuery()),
                Context(),
                CancellationToken.None);
            Assert.Equal(HostErrorCode.SessionClosed, statisticsRead.Error().Code);
            Assert.Equal(HostErrorCode.SessionClosed, processList.Error().Code);
            Assert.Equal(0, monitorFactory.Statistics(StatisticsSessionId).ReadCount);
            Assert.Equal(0, monitorFactory.Processes(ProcessSessionId).ListCount);

            await host.DisposeAsync();
            Assert.Equal(1, monitorFactory.Statistics(StatisticsSessionId).DisposeCount);
            Assert.Equal(1, monitorFactory.Processes(ProcessSessionId).DisposeCount);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ContextRevisionDeadlineAndCancellationAreCheckedBeforeDispatch()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var monitorFactory = new FakeSystemMonitorPanelSessionFactory();
        await using var host = CreateHost(monitorFactory, clock);
        var statistics = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureStatisticsSessionAsync(
                StatisticsRequest(),
                Context(),
                CancellationToken.None));
        _ = (await host.EnsureProcessMonitorSessionAsync(
            ProcessRequest(),
            Context(),
            CancellationToken.None)).Value();

        var stale = await host.ReadStatisticsAsync(
            StatisticsSessionId,
            Context(expectedRevision: statistics.ResultingRevision - 1),
            CancellationToken.None);
        var expired = await host.ReadStatisticsAsync(
            StatisticsSessionId,
            Context(deadline: clock.GetUtcNow()),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await host.ListProcessesAsync(
            new ProcessMonitorHostRequest(
                ProcessSessionId,
                new ProcessMonitorQuery()),
            Context(),
            cancellation.Token);

        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);
        Assert.Equal(HostErrorCode.DeadlineExceeded, expired.Error().Code);
        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.Equal(0, monitorFactory.Statistics(StatisticsSessionId).ReadCount);
        Assert.Equal(0, monitorFactory.Processes(ProcessSessionId).ListCount);
    }

    [Fact]
    public async Task CallerCancellationInterruptsAnActiveProcessList()
    {
        var monitorFactory = new FakeSystemMonitorPanelSessionFactory();
        await using var host = CreateHost(monitorFactory);
        _ = (await host.EnsureProcessMonitorSessionAsync(
            ProcessRequest(),
            Context(),
            CancellationToken.None)).Value();
        var session = monitorFactory.Processes(ProcessSessionId);
        session.BlockList = true;
        using var cancellation = new CancellationTokenSource();

        var listing = host.ListProcessesAsync(
            new ProcessMonitorHostRequest(
                ProcessSessionId,
                new ProcessMonitorQuery()),
            Context(),
            cancellation.Token).AsTask();
        await session.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await listing;

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal(1, session.ListCount);
    }

    private static InMemorySessionHostClient CreateHost(
        ISystemMonitorPanelSessionFactory? monitorFactory,
        TimeProvider? timeProvider = null) =>
        new(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            timeProvider ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            systemMonitorFactory: monitorFactory);

    private static EnsureStatisticsSessionRequest StatisticsRequest() =>
        new(StatisticsSessionId, Owner(StatisticsPanelId), "Statistics");

    private static EnsureProcessMonitorSessionRequest ProcessRequest() =>
        new(ProcessSessionId, Owner(ProcessPanelId), "Process monitor");

    private static SessionOwner Owner(PanelInstanceId panelId) =>
        new(
            HostMode.Desktop,
            WindowId,
            WorkspaceId,
            TabId,
            panelId);

    private static WorkspaceInstance Workspace()
    {
        var panels = new[]
        {
            new PanelInstance(
                StatisticsPanelId,
                PanelKind.Statistics,
                "Statistics"),
            new PanelInstance(
                ProcessPanelId,
                PanelKind.ProcessMonitor,
                "Process monitor"),
        };
        var tab = new TabInstance(TabId, "Monitoring", panels, StatisticsPanelId);
        return new WorkspaceInstance(WorkspaceId, "Monitoring", [tab], TabId);
    }

    private static CapabilitySet MonitorCapabilities() =>
        new(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.StatisticsRead,
            SessionCapabilities.ProcessesList,
        ]);

    private static OperationContext Context(
        long? expectedRevision = null,
        IdempotencyKey? idempotencyKey = null,
        DateTimeOffset? deadline = null) =>
        new(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("monitor-user"),
                ActorKind.Human,
                "Monitor user",
                new ClientId("monitor-client")),
            expectedRevision,
            idempotencyKey,
            CancellationId.New(),
            deadline);
}
