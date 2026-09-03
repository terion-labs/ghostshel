using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceIsolationEgressGuardTests
{
    private static readonly WorkspaceInstanceId WorkspaceInstanceId = new("running-workspace");
    private static readonly NetworkConnectionId ConnectionId = new("guarded-wireguard");

    [Fact]
    public async Task Arm_builds_an_isolate_local_namespace_with_a_fail_closed_forward_policy()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            Profile(),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Null(result.Error);
        var launch = Assert.Single(commands.Launches);
        var script = launch.Arguments[1];
        Assert.Contains("ip netns add", script, StringComparison.Ordinal);
        Assert.Contains("policy drop", script, StringComparison.Ordinal);
        Assert.Contains("table inet ghostshell_guard", script, StringComparison.Ordinal);
        Assert.Contains("meta mark set 0x4753", script, StringComparison.Ordinal);
        Assert.Contains("policy drop", script, StringComparison.Ordinal);
        Assert.Contains(
            "delete table inet ghostshell_guard",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ip route replace table 4242 default via 169.254.254.2 dev \"$main_veth\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "iifname \"gs-vpn\" oifname \"%s\" accept",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "iifname \"%s\" oifname \"gs-vpn\" ct state established,related accept",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "                    ct state established,related accept",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ip saddr 169.254.254.0/30 oifname != \"gs-main\" masquerade",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ip netns exec \"$namespace\" nft -f -",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            WorkspaceIsolationNetworkNames.TunnelInterface(ConnectionId),
            launch.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Missing_firewall_runtime_reports_the_required_workspace_packages()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(69, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            Profile(),
            CancellationToken.None);

        Assert.False(result.IsEnforced);
        var failure = Assert.IsType<NetworkConnectionError>(result.Error);
        Assert.Equal(NetworkConnectionErrorCode.RuntimeMissing, failure.Code);
        Assert.Equal(
            "workspace_network_guard_runtime_missing",
            failure.StableCode,
            StringComparer.Ordinal);
        Assert.Contains("iproute2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("nftables", failure.Message, StringComparison.Ordinal);
        Assert.Contains("procps", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_after_output_lockdown_reports_that_egress_is_still_enforced()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(78, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            Profile(),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Equal("workspace_network_kill_switch_arm_failed", result.Error?.StableCode);
    }

    [Fact]
    public async Task Disarm_removes_the_blocking_table_after_network_cleanup()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.DisarmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<Unit>.Success>(result);
        var script = Assert.Single(commands.Launches).Arguments[1];
        var routeCleanup = script.IndexOf("ip route flush table", StringComparison.Ordinal);
        var guardCleanup = script.IndexOf(
            "nft delete table inet ghostshell_guard",
            StringComparison.Ordinal);
        var cleanupFailureCheck = script.IndexOf(
            "if [ \"$cleanup_failed\" -ne 0 ]; then exit 70; fi",
            StringComparison.Ordinal);
        var stateCleanup = script.IndexOf("rm -rf -- \"$state\"", StringComparison.Ordinal);
        Assert.True(routeCleanup >= 0);
        Assert.True(cleanupFailureCheck > routeCleanup);
        Assert.True(guardCleanup > cleanupFailureCheck);
        Assert.True(guardCleanup > stateCleanup);
        Assert.True(guardCleanup > routeCleanup);
    }

    [Fact]
    public async Task Incomplete_disarm_is_a_typed_route_failure()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(70, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.DisarmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<Unit>.Failure>(result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.Equal("workspace_network_kill_switch_disarm_failed", failure.Error.StableCode);
    }

    private static NetworkConnectionProfile Profile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "WireGuard",
        new NetworkConnectionConfiguration.WireGuard(new SecretRef("wireguard-config")));

    private sealed class RecordingCommandRunner : IWorkspaceIsolationCommandRunner
    {
        public WorkspaceIsolationCommandResult Result { get; init; } =
            new(0, string.Empty, string.Empty);

        public List<WorkspaceProcessLaunch> Launches { get; } = [];

        public ValueTask<WorkspaceIsolationCommandResult> RunAsync(
            WorkspaceProcessLaunch launch,
            ReadOnlyMemory<byte> standardInput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launches.Add(launch);
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingIsolationProvider : IWorkspaceIsolationProvider
    {
        public WorkspaceIsolationProviderDescriptor Descriptor { get; } = new(
            new WorkspaceIsolationProviderId("test-isolation"),
            "Test isolation",
            WorkspaceIsolationCapability.DedicatedNetworkNamespace
            | WorkspaceIsolationCapability.StructuredProcessExecution);

        public WorkspaceIsolationBinding Binding { get; }

        public RecordingIsolationProvider()
        {
            Binding = new WorkspaceIsolationBinding(
                new WorkspaceId("workspace-definition"),
                Descriptor.Id,
                Descriptor.Capabilities,
                "test-isolate",
                [],
                Guid.NewGuid());
        }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
            WorkspaceIsolationPrepareRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(Binding));

        public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
            WorkspaceIsolationBinding binding,
            WorkspaceIsolationProcessRequest request)
        {
            Assert.Same(Binding, binding);
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "fake-isolation-runtime",
                    request.Arguments,
                    request.Environment,
                    hostWorkingDirectory: null));
        }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
            WorkspaceIsolationBinding binding,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding));
    }
}
