using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceNetworkRuntimeTests
{
    private static readonly NetworkConnectionId ConnectionId = new("test-proxy");

    [Fact]
    public async Task Disabled_policy_stays_direct_without_starting_a_provider()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);

        await using var session = await runtime.OpenAsync(
            Request(NetworkPolicy.Direct, []),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
        Assert.Equal(0, provider.ConnectCount);
    }

    [Fact]
    public async Task Enabled_policy_publishes_the_provider_route()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = Profile();

        await using var session = await runtime.OpenAsync(
            Request(new NetworkPolicy([ConnectionId], ConnectionId, true, true), [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(ConnectionId, session.Snapshot.SelectedConnectionId);
        Assert.Equal(provider.Session.Egress, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Unstored_AnyConnect_password_is_prompted_for_each_new_connection()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        var prompt = new RecordingPasswordPrompt("session-password");
        var runtime = new WorkspaceNetworkRuntime(
            [provider],
            passwordPrompt: prompt);
        var profile = AnyConnectProfile();
        var request = Request(
            new NetworkPolicy([ConnectionId], ConnectionId, true, false),
            [profile]);

        await using var first = await runtime.OpenAsync(
            request,
            progress: null,
            CancellationToken.None);
        await using var second = await runtime.OpenAsync(
            request,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, prompt.Requests.Count);
        Assert.All(
            provider.Passwords,
            password => Assert.Equal("session-password", Encoding.UTF8.GetString(password!)));
        Assert.True(provider.LastRequest?.TransientPassword?.IsDisposed);
    }

    [Fact]
    public async Task Stored_AnyConnect_password_does_not_open_the_password_prompt()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        var prompt = new RecordingPasswordPrompt("unused");
        var runtime = new WorkspaceNetworkRuntime(
            [provider],
            passwordPrompt: prompt);
        var profile = AnyConnectProfile(new SecretRef("stored-password"));

        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Empty(prompt.Requests);
        Assert.Null(provider.Passwords.Single());
    }

    [Fact]
    public async Task Cancelling_the_password_prompt_does_not_start_the_provider()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        var prompt = new RecordingPasswordPrompt("unused", cancel: true);
        var runtime = new WorkspaceNetworkRuntime(
            [provider],
            passwordPrompt: prompt);
        var profile = AnyConnectProfile();

        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(NetworkConnectionErrorCode.Cancelled, session.Snapshot.Error?.Code);
        Assert.Equal(0, provider.ConnectCount);
    }

    [Fact]
    public async Task Unstored_isolated_proxy_password_is_passed_to_the_guest_guard()
    {
        var guard = new RecordingEgressGuard();
        var prompt = new RecordingPasswordPrompt("proxy-password");
        var runtime = new WorkspaceNetworkRuntime(
            [],
            guard,
            prompt);
        var profile = new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Authenticated proxy",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "proxy.example.test",
                1080,
                username: "proxy-user"));

        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal("proxy-password", Encoding.UTF8.GetString(guard.TransientPassword!));
        Assert.Single(prompt.Requests);
    }

    [Fact]
    public async Task Missing_provider_falls_back_to_direct_without_a_kill_switch()
    {
        var runtime = new WorkspaceNetworkRuntime([]);
        var profile = Profile();

        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Direct, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Missing_provider_blocks_a_host_workspace_with_a_kill_switch()
    {
        var runtime = new WorkspaceNetworkRuntime([]);
        var profile = Profile();

        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Provider_failure_blocks_a_kill_switched_host_workspace()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = Profile();
        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        provider.Session.Publish(NetworkConnectionState.Failed, "Proxy stopped.");

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal("Proxy stopped.", session.Snapshot.Error?.Message);
    }

    [Fact]
    public async Task Disabling_an_active_policy_disposes_the_provider_session()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = Profile();
        await using var session = await runtime.OpenAsync(
            Request(new NetworkPolicy([ConnectionId], ConnectionId, true, true), [profile]),
            progress: null,
            CancellationToken.None);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(
                new NetworkPolicy([ConnectionId], ConnectionId, false, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success>(result);
        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
        Assert.True(provider.Session.IsDisposed);
    }

    [Fact]
    public async Task Failed_provider_cleanup_does_not_publish_direct_egress()
    {
        var provider = new RecordingProvider(
            NetworkConnectionKind.WireGuard,
            failCleanup: true);
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, [profile]),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Failure>(
            result);
        Assert.Equal("network_connection_cleanup_failed", failure.Error.StableCode);
        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Attached, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Failed_provider_cleanup_keeps_an_armed_kill_switch()
    {
        var provider = new RecordingProvider(
            NetworkConnectionKind.WireGuard,
            failCleanup: true);
        var guard = new RecordingEgressGuard();
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, [profile]),
            progress: null,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Failure>(result);
        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal(0, guard.DisarmCount);
    }

    [Fact]
    public async Task Provider_failure_blocks_a_kill_switched_workspace()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var guard = new RecordingEgressGuard();
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        provider.Session.Publish(NetworkConnectionState.Failed, "Tunnel stopped.");

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal("Tunnel stopped.", session.Snapshot.Error?.Message);
    }

    [Fact]
    public async Task Provider_failure_without_a_kill_switch_does_not_claim_direct_egress()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        provider.Session.Publish(NetworkConnectionState.Failed, "Tunnel stopped.");

        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Attached, session.Snapshot.Egress);
        Assert.Equal("Tunnel stopped.", session.Snapshot.Error?.Message);
    }

    [Fact]
    public async Task Failed_vpn_attach_keeps_isolate_egress_blocked_until_policy_is_disabled()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard, failConnect: true);
        var guard = new RecordingEgressGuard();
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal(1, guard.ArmCount);
        Assert.Equal(0, guard.DisarmCount);
        Assert.True(provider.LastRequest?.KillSwitchEnabled);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, [profile]),
            progress: null,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success>(result);
        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
        Assert.Equal(1, guard.DisarmCount);
    }

    [Fact]
    public async Task Disposing_after_a_failed_vpn_attach_releases_the_isolate_guard()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard, failConnect: true);
        var guard = new RecordingEgressGuard();
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        await session.DisposeAsync();

        Assert.Equal(1, guard.ArmCount);
        Assert.Equal(1, guard.DisarmCount);
    }

    [Fact]
    public async Task Vpn_without_kill_switch_does_not_arm_the_isolate_guard()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var guard = new RecordingEgressGuard();
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(0, guard.ArmCount);
        Assert.False(provider.LastRequest?.KillSwitchEnabled);
    }

    [Fact]
    public async Task Isolated_proxy_is_enforced_in_the_guest_without_starting_host_provider()
    {
        var provider = new RecordingProvider();
        var guard = new RecordingEgressGuard();
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = Profile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Attached, session.Snapshot.Egress);
        Assert.Equal(1, guard.ArmCount);
        Assert.Equal(0, provider.ConnectCount);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, [profile]),
            progress: null,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success>(result);
        Assert.Equal(1, guard.DisarmCount);
        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
    }

    [Fact]
    public async Task Failed_isolated_proxy_setup_restores_direct_route_without_a_kill_switch()
    {
        var guard = new RecordingEgressGuard { FailArm = true };
        var runtime = new WorkspaceNetworkRuntime([], guard);
        var profile = Profile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Direct, session.Snapshot.Egress);
        Assert.Equal(1, guard.ArmCount);
        Assert.Equal(1, guard.DisarmCount);
    }

    [Fact]
    public async Task Uncertain_isolated_proxy_failure_stays_blocked_when_cleanup_fails()
    {
        var guard = new RecordingEgressGuard
        {
            FailArm = true,
            EnforceBeforeArmFailure = true,
            FailDisarm = true,
        };
        var runtime = new WorkspaceNetworkRuntime([], guard);
        var profile = Profile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal(1, guard.ArmCount);
        Assert.Equal(1, guard.DisarmCount);
    }

    [Fact]
    public async Task Failed_guard_teardown_does_not_claim_direct_egress()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard, failConnect: true);
        var guard = new RecordingEgressGuard { FailDisarm = true };
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, [profile]),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Failure>(
            result);
        Assert.Equal(
            "test_guard_disarm_failed",
            failure.Error.StableCode,
            StringComparer.Ordinal);
        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Guard_failure_before_enforcement_does_not_claim_blocked_egress()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var guard = new RecordingEgressGuard { FailArm = true };
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Direct, session.Snapshot.Egress);
        Assert.Equal(0, provider.ConnectCount);
    }

    [Fact]
    public async Task Cleanup_failure_after_unenforced_arm_failure_does_not_claim_blocked_egress()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var guard = new RecordingEgressGuard { FailArm = true, FailDisarm = true };
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, [profile]),
            progress: null,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Failure>(result);
        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Direct, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Guard_failure_after_enforcement_keeps_the_workspace_blocked()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var guard = new RecordingEgressGuard
        {
            FailArm = true,
            EnforceBeforeArmFailure = true,
        };
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        await using var session = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal(0, provider.ConnectCount);
    }

    [Fact]
    public async Task Windows_sharing_one_persistent_isolate_share_one_network_session()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var guard = new RecordingEgressGuard();
        var runtime = new WorkspaceNetworkRuntime([provider], guard);
        var profile = VpnProfile();
        var binding = Binding();
        var secondBinding = new WorkspaceIsolationBinding(
            binding.WorkspaceId,
            binding.Provider,
            binding.Capabilities,
            binding.ResourceName,
            binding.Mounts,
            Guid.NewGuid());
        var first = await runtime.OpenAsync(
            IsolatedRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [profile],
                binding),
            progress: null,
            CancellationToken.None);
        var second = await runtime.OpenAsync(
            IsolatedRequest(NetworkPolicy.Direct, [profile], secondBinding),
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, provider.ConnectCount);
        Assert.Equal(1, guard.ArmCount);
        Assert.Equal(WorkspaceNetworkState.Connected, second.Snapshot.State);

        await first.DisposeAsync();

        Assert.False(provider.Session.IsDisposed);
        Assert.Equal(0, guard.DisarmCount);

        await second.DisposeAsync();

        Assert.True(provider.Session.IsDisposed);
        Assert.Equal(1, guard.DisarmCount);
    }

    [Fact]
    public async Task Reopening_an_isolate_waits_for_the_previous_session_to_finish_closing()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = VpnProfile();
        var binding = Binding();
        var request = IsolatedRequest(
            new NetworkPolicy([ConnectionId], ConnectionId, true, false),
            [profile],
            binding);
        var first = await runtime.OpenAsync(request, progress: null, CancellationToken.None);
        provider.Session.BlockDisposal();

        var closing = first.DisposeAsync().AsTask();
        await provider.Session.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(1));
        var reopening = runtime.OpenAsync(request, progress: null, CancellationToken.None).AsTask();

        Assert.False(reopening.IsCompleted);
        Assert.Equal(1, provider.ConnectCount);

        provider.Session.AllowDisposal();
        await closing;
        await using var second = await reopening;

        Assert.Equal(2, provider.ConnectCount);
    }

    [Fact]
    public async Task Different_persistent_isolates_keep_independent_network_sessions()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.WireGuard);
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = VpnProfile();
        var policy = new NetworkPolicy([ConnectionId], ConnectionId, true, false);
        var first = await runtime.OpenAsync(
            IsolatedRequest(policy, [profile], Binding("first-isolate")),
            progress: null,
            CancellationToken.None);
        var second = await runtime.OpenAsync(
            IsolatedRequest(policy, [profile], Binding("second-isolate")),
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, provider.ConnectCount);

        await first.DisposeAsync();

        Assert.True(provider.Sessions[0].IsDisposed);
        Assert.False(provider.Sessions[1].IsDisposed);

        await second.DisposeAsync();

        Assert.True(provider.Sessions[1].IsDisposed);
    }

    private static WorkspaceNetworkOpenRequest Request(
        NetworkPolicy policy,
        IReadOnlyList<NetworkConnectionProfile> profiles) => new(
        new WorkspaceInstanceId("running-workspace"),
        new WorkspaceNetworkPolicyUpdate(policy, profiles),
        WorkspaceNetworkPlacement.Host);

    private static WorkspaceNetworkOpenRequest IsolatedRequest(
        NetworkPolicy policy,
        IReadOnlyList<NetworkConnectionProfile> profiles,
        WorkspaceIsolationBinding? binding = null) => new(
        new WorkspaceInstanceId("running-workspace"),
        new WorkspaceNetworkPolicyUpdate(policy, profiles),
        WorkspaceNetworkPlacement.Isolated(binding ?? Binding()));

    private static NetworkConnectionProfile Profile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Test proxy",
        new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));

    private static NetworkConnectionProfile VpnProfile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Test WireGuard",
        new NetworkConnectionConfiguration.WireGuard(new SecretRef("wireguard-config")));

    private static NetworkConnectionProfile AnyConnectProfile(
        SecretRef? password = null) => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Test AnyConnect",
        new NetworkConnectionConfiguration.AnyConnect(
            new Uri("https://vpn.example.test"),
            username: "test-user",
            passwordSecret: password));

    private static WorkspaceIsolationBinding Binding(string resourceName = "test-isolate") => new(
        new WorkspaceId("workspace-definition"),
        new WorkspaceIsolationProviderId("test-isolation"),
        WorkspaceIsolationCapability.DedicatedNetworkNamespace
        | WorkspaceIsolationCapability.StructuredProcessExecution,
        resourceName,
        [],
        Guid.NewGuid());

    private sealed class RecordingProvider(
        NetworkConnectionKind kind = NetworkConnectionKind.Proxy,
        bool failConnect = false,
        bool failCleanup = false) : INetworkConnectionProvider
    {
        public NetworkConnectionKind Kind => kind;

        public List<RecordingSession> Sessions { get; } = [];

        public RecordingSession Session => Sessions[0];

        public int ConnectCount { get; private set; }

        public NetworkConnectionStartRequest? LastRequest { get; private set; }

        public List<byte[]?> Passwords { get; } = [];

        public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            LastRequest = request;
            if (request.TransientPassword is { } transientPassword)
            {
                var bytes = new byte[transientPassword.Length];
                transientPassword.CopyTo(bytes);
                Passwords.Add(bytes);
            }
            else
            {
                Passwords.Add(null);
            }
            if (failConnect)
            {
                return ValueTask.FromResult(
                    NetworkConnectionResult<INetworkConnectionSession>.Fail(
                        new NetworkConnectionError(
                            NetworkConnectionErrorCode.ConnectionFailed,
                            "test_vpn_attach_failed",
                            "The test VPN could not connect.",
                            retryable: true)));
            }

            INetworkConnectionSession session = new RecordingSession(
                ConnectionId,
                kind == NetworkConnectionKind.Proxy
                    ? WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:43123"))
                    : WorkspaceNetworkEgress.Attached,
                failCleanup);
            Sessions.Add((RecordingSession)session);
            return ValueTask.FromResult(
                NetworkConnectionResult<INetworkConnectionSession>.Succeed(session));
        }
    }

    private sealed class RecordingEgressGuard : IWorkspaceIsolationEgressGuard
    {
        public int ArmCount { get; private set; }

        public int DisarmCount { get; private set; }

        public bool FailDisarm { get; init; }

        public bool FailArm { get; init; }

        public bool EnforceBeforeArmFailure { get; init; }

        public byte[]? TransientPassword { get; private set; }

        public ValueTask<WorkspaceIsolationEgressGuardArmResult> ArmAsync(
            WorkspaceInstanceId workspaceId,
            WorkspaceIsolationBinding binding,
            NetworkConnectionProfile connection,
            CancellationToken cancellationToken,
            SecretMaterial? transientPassword = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArmCount++;
            if (transientPassword is not null)
            {
                TransientPassword = new byte[transientPassword.Length];
                transientPassword.CopyTo(TransientPassword);
            }
            return ValueTask.FromResult(FailArm
                ? WorkspaceIsolationEgressGuardArmResult.Failed(
                    new NetworkConnectionError(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "test_guard_arm_failed",
                        "The test guard could not be enabled.",
                        retryable: true),
                    EnforceBeforeArmFailure)
                : WorkspaceIsolationEgressGuardArmResult.Enforced());
        }

        public ValueTask<NetworkConnectionResult<Unit>> DisarmAsync(
            WorkspaceInstanceId workspaceId,
            WorkspaceIsolationBinding binding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisarmCount++;
            if (FailDisarm)
            {
                return ValueTask.FromResult(NetworkConnectionResult<Unit>.Fail(
                    new NetworkConnectionError(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "test_guard_disarm_failed",
                        "The test guard could not be removed.",
                        retryable: true)));
            }

            return ValueTask.FromResult(NetworkConnectionResult<Unit>.Succeed(Unit.Value));
        }
    }

    private sealed class RecordingPasswordPrompt(
        string password,
        bool cancel = false) : INetworkPasswordPrompt
    {
        public List<NetworkPasswordPromptRequest> Requests { get; } = [];

        public ValueTask<NetworkConnectionResult<SecretMaterial>> RequestPasswordAsync(
            NetworkPasswordPromptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (cancel)
            {
                return ValueTask.FromResult(
                    NetworkConnectionResult<SecretMaterial>.Fail(
                        new NetworkConnectionError(
                            NetworkConnectionErrorCode.Cancelled,
                            "test_password_cancelled",
                            "The test password prompt was cancelled.",
                            retryable: true)));
            }

            return ValueTask.FromResult(
                NetworkConnectionResult<SecretMaterial>.Succeed(
                    SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(password))));
        }
    }

    private sealed class RecordingSession(
        NetworkConnectionId connectionId,
        WorkspaceNetworkEgress egress,
        bool failCleanup) :
        INetworkConnectionSession
    {
        private readonly TaskCompletionSource _disposeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _disposeRelease;

        public bool IsDisposed { get; private set; }

        public Task DisposeStarted => _disposeStarted.Task;

        public NetworkConnectionSnapshot Snapshot { get; private set; } = new(
            connectionId,
            NetworkConnectionState.Connected);

        public WorkspaceNetworkEgress Egress { get; } = egress;

        public event EventHandler<NetworkConnectionSnapshot>? Changed;

        public void Publish(NetworkConnectionState state, string? status)
        {
            Snapshot = new NetworkConnectionSnapshot(connectionId, state, status);
            Changed?.Invoke(this, Snapshot);
        }

        public void BlockDisposal() => _disposeRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AllowDisposal() => _disposeRelease?.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _disposeStarted.TrySetResult();
            if (_disposeRelease is { } release)
            {
                await release.Task.ConfigureAwait(false);
            }

            if (failCleanup)
            {
                Publish(NetworkConnectionState.Failed, "The test route cleanup failed.");
            }
        }
    }
}
