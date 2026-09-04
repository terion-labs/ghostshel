using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class IsolatedVpnConnectionProviderTests
{
    private static readonly NetworkConnectionId ConnectionId = new("isolated-vpn-test");

    [Theory]
    [InlineData(NetworkConnectionKind.WireGuard)]
    [InlineData(NetworkConnectionKind.OpenVpn)]
    [InlineData(NetworkConnectionKind.AnyConnect)]
    [InlineData(NetworkConnectionKind.Tailscale)]
    public async Task Supported_vpn_attaches_only_inside_the_isolation_binding(
        NetworkConnectionKind kind)
    {
        using var vault = new InMemorySecretVault();
        var configuration = await ConfigurationAsync(kind, vault);
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var provider = new IsolatedVpnConnectionProvider(kind, vault, isolation, commands);

        await using var session = Success(await provider.ConnectAsync(
            Request(configuration, WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None));

        Assert.Equal(WorkspaceNetworkEgress.Attached, session.Egress);
        Assert.Equal(NetworkConnectionState.Connected, session.Snapshot.State);
        Assert.All(
            isolation.Requests,
            request =>
            {
                Assert.Equal(ConnectionKind.Local, request.ConnectionKind);
                Assert.Equal("/bin/sh", request.HostExecutable);
            });
        Assert.DoesNotContain(
            commands.Launches.SelectMany(launch => launch.Arguments),
            argument => argument.Contains("vpn-secret-value", StringComparison.Ordinal));

        await session.DisposeAsync();

        Assert.Equal(NetworkConnectionState.Disconnected, session.Snapshot.State);
        Assert.Contains(
            commands.Scripts,
            script => script.Contains("rm -rf", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NetworkConnectionKind.WireGuard, "in_vpn wg-quick up")]
    [InlineData(NetworkConnectionKind.OpenVpn, "in_vpn openvpn")]
    [InlineData(NetworkConnectionKind.AnyConnect, "in_vpn openconnect")]
    [InlineData(NetworkConnectionKind.Tailscale, "in_vpn tailscale")]
    public async Task Vpn_engine_is_launched_in_the_guard_network_namespace_when_present(
        NetworkConnectionKind kind,
        string expectedLaunch)
    {
        using var vault = new InMemorySecretVault();
        var configuration = await ConfigurationAsync(kind, vault);
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var provider = new IsolatedVpnConnectionProvider(kind, vault, isolation, commands);

        await using var session = Success(await provider.ConnectAsync(
            Request(configuration, WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None));

        Assert.Contains(
            commands.Scripts,
            script => script.Contains(expectedLaunch, StringComparison.Ordinal)
                && script.Contains(
                    "ip netns exec ghostshell-vpn",
                    StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NetworkConnectionKind.WireGuard)]
    [InlineData(NetworkConnectionKind.OpenVpn)]
    [InlineData(NetworkConnectionKind.AnyConnect)]
    [InlineData(NetworkConnectionKind.Tailscale)]
    public async Task Host_placement_fails_without_starting_a_system_vpn(
        NetworkConnectionKind kind)
    {
        using var vault = new InMemorySecretVault();
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var provider = new IsolatedVpnConnectionProvider(kind, vault, isolation, commands);
        var configuration = ConfigurationWithoutStoredSecret(kind);

        var result = await provider.ConnectAsync(
            Request(configuration, WorkspaceNetworkPlacement.Host),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.EndsWith("_host_userspace_unavailable", failure.Error.StableCode, StringComparison.Ordinal);
        Assert.Empty(isolation.Requests);
        Assert.Empty(commands.Launches);
    }

    [Fact]
    public async Task Placement_without_a_dedicated_network_namespace_is_rejected()
    {
        using var vault = new InMemorySecretVault();
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.Tailscale,
            vault,
            isolation,
            commands);
        var sharedNetworkBinding = new WorkspaceIsolationBinding(
            isolation.Binding.WorkspaceId,
            isolation.Binding.Provider,
            WorkspaceIsolationCapability.StructuredProcessExecution,
            isolation.Binding.ResourceName,
            [],
            Guid.NewGuid());

        var result = await provider.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.Tailscale("exit-node"),
                WorkspaceNetworkPlacement.Isolated(sharedNetworkBinding)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.Equal("tailscale_dedicated_network_unavailable", failure.Error.StableCode);
        Assert.Empty(commands.Launches);
    }

    [Theory]
    [InlineData(NetworkConnectionKind.WireGuard, "wireguard-tools")]
    [InlineData(NetworkConnectionKind.OpenVpn, "openvpn package")]
    [InlineData(NetworkConnectionKind.AnyConnect, "openconnect package")]
    [InlineData(NetworkConnectionKind.Tailscale, "tailscale and tailscaled")]
    public async Task Missing_guest_runtime_reports_the_concrete_package(
        NetworkConnectionKind kind,
        string expectedPackage)
    {
        using var vault = new InMemorySecretVault();
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        commands.Enqueue(new WorkspaceIsolationCommandResult(69, string.Empty, string.Empty));
        var provider = new IsolatedVpnConnectionProvider(kind, vault, isolation, commands);

        var result = await provider.ConnectAsync(
            Request(
                ConfigurationWithoutStoredSecret(kind),
                WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RuntimeMissing, failure.Error.Code);
        Assert.Contains(expectedPackage, failure.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(commands.Launches);
    }

    [Fact]
    public async Task Missing_health_probe_runtime_is_reported_before_secret_access()
    {
        using var vault = new InMemorySecretVault();
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        commands.Enqueue(new WorkspaceIsolationCommandResult(68, string.Empty, string.Empty));
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.WireGuard,
            vault,
            isolation,
            commands);

        var result = await provider.ConnectAsync(
            Request(
                ConfigurationWithoutStoredSecret(NetworkConnectionKind.WireGuard),
                WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RuntimeMissing, failure.Error.Code);
        Assert.Equal("wireguard_health_probe_runtime_missing", failure.Error.StableCode);
        Assert.Contains("curl", failure.Error.Message, StringComparison.Ordinal);
        Assert.Single(commands.Launches);
    }

    [Fact]
    public async Task WireGuard_configuration_is_resolved_from_its_network_connection_scope()
    {
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("wrong-scope-wireguard-config");
        await StoreSecretAsync(
            vault,
            reference,
            "vpn-secret-value",
            new SecretScope(SecretScopeKind.NetworkConnection, "another-connection"));
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.WireGuard,
            vault,
            isolation,
            commands);

        var result = await provider.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.WireGuard(reference),
                WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.AuthenticationRequired, failure.Error.Code);
        Assert.Equal("wireguard_secret_access_required", failure.Error.StableCode);
    }

    [Fact]
    public async Task AnyConnect_requires_unattended_credentials_before_running_OpenConnect()
    {
        using var vault = new InMemorySecretVault();
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.AnyConnect,
            vault,
            isolation,
            commands);

        var result = await provider.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test")),
                WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.AuthenticationRequired, failure.Error.Code);
        Assert.Equal("anyconnect_credentials_required", failure.Error.StableCode);
        Assert.Empty(commands.Launches);
    }

    [Fact]
    public async Task OpenConnect_authentication_failure_is_typed_and_runs_cleanup()
    {
        using var vault = new InMemorySecretVault();
        var password = new SecretRef("anyconnect-password");
        await StoreSecretAsync(vault, password, "vpn-secret-value", Scope());
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.Enqueue(new WorkspaceIsolationCommandResult(
            1,
            string.Empty,
            "Authentication failed"));
        commands.EnqueueSuccess();
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.AnyConnect,
            vault,
            isolation,
            commands);

        var result = await provider.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test"),
                    username: "test-user",
                    passwordSecret: password),
                WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.AuthenticationRequired, failure.Error.Code);
        Assert.Equal("anyconnect_authentication_failed", failure.Error.StableCode);
        Assert.Contains(
            "openconnect.pid",
            commands.Scripts.Last(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_guest_command_returns_a_typed_cancelled_result()
    {
        using var vault = new InMemorySecretVault();
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner { ThrowCancellation = true };
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.Tailscale,
            vault,
            isolation,
            commands);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await provider.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.Tailscale("exit-node"),
                WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            cancellation.Token);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.Cancelled, failure.Error.Code);
    }

    [Theory]
    [InlineData(NetworkConnectionKind.WireGuard)]
    [InlineData(NetworkConnectionKind.OpenVpn)]
    [InlineData(NetworkConnectionKind.AnyConnect)]
    [InlineData(NetworkConnectionKind.Tailscale)]
    public async Task Attach_and_health_checks_require_full_ipv4_and_available_ipv6_routes(
        NetworkConnectionKind kind)
    {
        using var vault = new InMemorySecretVault();
        var configuration = await ConfigurationAsync(kind, vault);
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var provider = new IsolatedVpnConnectionProvider(kind, vault, isolation, commands);

        await using var session = Success(await provider.ConnectAsync(
            Request(configuration, WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None));

        var routeScripts = commands.Scripts.Where(
            script => script.Contains("require_full_route", StringComparison.Ordinal));
        Assert.Contains(
            routeScripts,
            script => script.Contains("0.0.0.0/1", StringComparison.Ordinal)
                && script.Contains("128.0.0.0/1", StringComparison.Ordinal)
                && script.Contains("::/1", StringComparison.Ordinal)
                && script.Contains("8000::/1", StringComparison.Ordinal));
        Assert.Contains(
            isolation.Requests,
            request => request.Arguments[1].Contains(
                "has_non_tunnel_default 6",
                StringComparison.Ordinal));
        Assert.Contains(
            isolation.Requests,
            request => request.Arguments[1].Contains(
                    "curl --interface \"$iface\"",
                    StringComparison.Ordinal)
                && request.Arguments[1].Contains(
                    "--noproxy '*'",
                    StringComparison.Ordinal)
                && request.Arguments[1].Contains(
                    "https://1.1.1.1/cdn-cgi/trace",
                    StringComparison.Ordinal)
                && request.Arguments[1].Contains(
                    "https://1.0.0.1/cdn-cgi/trace",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreachable_route_is_rejected_before_the_session_is_connected()
    {
        using var vault = new InMemorySecretVault();
        var configuration = await ConfigurationAsync(NetworkConnectionKind.WireGuard, vault);
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.Enqueue(new WorkspaceIsolationCommandResult(65, string.Empty, string.Empty));
        commands.EnqueueSuccess();
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.WireGuard,
            vault,
            isolation,
            commands);

        var result = await provider.ConnectAsync(
            Request(configuration, WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.Equal("wireguard_reachability_failed", failure.Error.StableCode);
        Assert.Contains("cannot carry", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wg-quick down", commands.Scripts.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_health_probe_marks_the_connected_session_failed()
    {
        using var vault = new InMemorySecretVault();
        var configuration = await ConfigurationAsync(NetworkConnectionKind.WireGuard, vault);
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.Enqueue(new WorkspaceIsolationCommandResult(65, string.Empty, string.Empty));
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.WireGuard,
            vault,
            isolation,
            commands,
            TimeSpan.FromMilliseconds(10));
        await using var session = Success(await provider.ConnectAsync(
            Request(configuration, WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None));
        var failed = new TaskCompletionSource<NetworkConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.State == NetworkConnectionState.Failed)
            {
                failed.TrySetResult(snapshot);
            }
        };

        var snapshot = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NetworkConnectionState.Failed, snapshot.State);
        Assert.Contains("carry", snapshot.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cleanup_failure_is_exposed_by_the_session()
    {
        using var vault = new InMemorySecretVault();
        var configuration = await ConfigurationAsync(NetworkConnectionKind.OpenVpn, vault);
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.EnqueueSuccess();
        commands.Enqueue(new WorkspaceIsolationCommandResult(70, string.Empty, string.Empty));
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.OpenVpn,
            vault,
            isolation,
            commands);
        var session = Success(await provider.ConnectAsync(
            Request(configuration, WorkspaceNetworkPlacement.Isolated(isolation.Binding)),
            progress: null,
            CancellationToken.None));

        await session.DisposeAsync();

        Assert.Equal(NetworkConnectionState.Failed, session.Snapshot.State);
        Assert.Contains("cleanup failed", session.Snapshot.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "stop_expected_process \"$pid\" openvpn",
            commands.Scripts.Last(),
            StringComparison.Ordinal);
        Assert.Contains(
            "/proc/$process/comm",
            commands.Scripts.Last(),
            StringComparison.Ordinal);
    }

    private static async Task<NetworkConnectionConfiguration> ConfigurationAsync(
        NetworkConnectionKind kind,
        InMemorySecretVault vault)
    {
        var first = new SecretRef($"{kind}-first-secret");
        await StoreSecretAsync(vault, first, "vpn-secret-value", Scope());
        if (kind == NetworkConnectionKind.AnyConnect)
        {
            var second = new SecretRef("anyconnect-certificate");
            await StoreSecretAsync(vault, second, "vpn-secret-value", Scope());
            return new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example.test"),
                "test-user",
                first,
                "employees",
                second);
        }

        return kind switch
        {
            NetworkConnectionKind.WireGuard =>
                new NetworkConnectionConfiguration.WireGuard(first),
            NetworkConnectionKind.OpenVpn =>
                new NetworkConnectionConfiguration.OpenVpn(first),
            NetworkConnectionKind.Tailscale =>
                new NetworkConnectionConfiguration.Tailscale("exit-node", authKeySecret: first),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static NetworkConnectionConfiguration ConfigurationWithoutStoredSecret(
        NetworkConnectionKind kind) => kind switch
        {
            NetworkConnectionKind.WireGuard =>
                new NetworkConnectionConfiguration.WireGuard(new SecretRef("wireguard-config")),
            NetworkConnectionKind.OpenVpn =>
                new NetworkConnectionConfiguration.OpenVpn(new SecretRef("openvpn-config")),
            NetworkConnectionKind.AnyConnect =>
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test"),
                    passwordSecret: new SecretRef("anyconnect-password")),
            NetworkConnectionKind.Tailscale =>
                new NetworkConnectionConfiguration.Tailscale("exit-node"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static NetworkConnectionStartRequest Request(
        NetworkConnectionConfiguration configuration,
        WorkspaceNetworkPlacement placement) => new(
        new WorkspaceInstanceId("running-workspace"),
        new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Test VPN",
            configuration),
        placement,
        killSwitchEnabled: false);

    private static SecretScope Scope() =>
        new(SecretScopeKind.NetworkConnection, ConnectionId.Value);

    private static async Task StoreSecretAsync(
        InMemorySecretVault vault,
        SecretRef reference,
        string value,
        SecretScope scope)
    {
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(value));
        _ = Success(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "VPN test secret",
                SecretKind.Other,
                scope,
                new SecretUsePurpose(SecretUseKind.UserManagement, scope.OwnerId!)),
            material,
            CancellationToken.None));
    }

    private static T Success<T>(NetworkConnectionResult<T> result) =>
        Assert.IsType<NetworkConnectionResult<T>.Success>(result).Value;

    private static T Success<T>(SecretVaultResult<T> result) =>
        Assert.IsType<SecretVaultResult<T>.Success>(result).Value;

    private sealed class RecordingCommandRunner : IWorkspaceIsolationCommandRunner
    {
        private readonly Queue<WorkspaceIsolationCommandResult> _results = new();

        public List<WorkspaceProcessLaunch> Launches { get; } = [];

        public IReadOnlyList<string> Scripts =>
            [.. Launches.Select(launch => launch.Arguments[1])];

        public bool ThrowCancellation { get; init; }

        public void Enqueue(WorkspaceIsolationCommandResult result) => _results.Enqueue(result);

        public void EnqueueSuccess() =>
            Enqueue(new WorkspaceIsolationCommandResult(0, string.Empty, string.Empty));

        public ValueTask<WorkspaceIsolationCommandResult> RunAsync(
            WorkspaceProcessLaunch launch,
            ReadOnlyMemory<byte> standardInput,
            CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            if (ThrowCancellation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return ValueTask.FromResult(_results.Count == 0
                ? new WorkspaceIsolationCommandResult(0, string.Empty, string.Empty)
                : _results.Dequeue());
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

        public List<WorkspaceIsolationProcessRequest> Requests { get; } = [];

        public RecordingIsolationProvider()
        {
            Binding = new WorkspaceIsolationBinding(
                new WorkspaceId("workspace-definition"),
                Descriptor.Id,
                Descriptor.Capabilities,
                "test-workspace-isolate",
                [],
                Guid.NewGuid());
        }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
            WorkspaceIsolationPrepareRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(Binding));

        public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
            WorkspaceIsolationBinding binding,
            WorkspaceIsolationProcessRequest request)
        {
            Assert.Same(Binding, binding);
            Requests.Add(request);
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
            ValueTask.FromResult(WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding));
    }
}
