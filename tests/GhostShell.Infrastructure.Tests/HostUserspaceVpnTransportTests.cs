using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class HostUserspaceVpnTransportTests
{
    private static readonly NetworkConnectionId ConnectionId = new("host-vpn-test");
    private static readonly WorkspaceInstanceId WorkspaceId = new("workspace-instance");

    [Fact]
    public async Task WireGuard_exposes_only_a_loopback_Socks_route_and_cleans_up_secrets()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            ("wireproxy", "/tools/wireproxy"));

        var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
            progress: null,
            CancellationToken.None));

        Assert.NotNull(session.Egress.ProxyEndpoint);
        Assert.Equal("socks5", session.Egress.ProxyEndpoint.Scheme);
        var validation = Assert.Single(processes.Commands);
        Assert.Equal(["-c", validation.Arguments[1], "-n"], validation.Arguments);
        var started = Assert.Single(processes.Starts);
        Assert.Equal(["-c", started.Arguments[1], "-s"], started.Arguments);
        Assert.DoesNotContain(
            started.Arguments,
            argument => argument.Contains("secret", StringComparison.Ordinal));
        Assert.Contains("[Socks5]", await File.ReadAllTextAsync(started.Arguments[1]));
        var temporaryDirectory = Path.GetDirectoryName(started.Arguments[1])!;

        await session.DisposeAsync();

        Assert.False(Directory.Exists(temporaryDirectory));
    }

    [Fact]
    public async Task WireGuard_retries_when_the_first_userspace_process_loses_the_port_race()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-retry-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner();
        processes.ListenerResults.Enqueue(false);
        processes.ListenerResults.Enqueue(true);
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            ("wireproxy", "/tools/wireproxy"));

        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
            progress: null,
            CancellationToken.None));

        Assert.Equal(2, processes.Starts.Count);
        Assert.Equal(2, processes.Commands.Count);
    }

    [Fact]
    public async Task AnyConnect_uses_script_tun_and_keeps_the_password_out_of_arguments()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var password = new SecretRef("anyconnect-password");
        await StoreSecretAsync(vault, password, "secret-password");
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.AnyConnect,
            vault,
            processes,
            state.Path,
            ("openconnect", "/tools/openconnect"),
            ("ocproxy", "/tools/ocproxy"));

        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example.test"),
                username: "user",
                passwordSecret: password)),
            progress: null,
            CancellationToken.None));

        var started = Assert.Single(processes.Starts);
        Assert.Contains("--script-tun", started.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            started.Arguments,
            argument => argument.StartsWith("'/tools/ocproxy' -D ", StringComparison.Ordinal));
        Assert.Contains("--passwd-on-stdin", started.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--interface", started.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(
            started.Arguments,
            argument => argument.Contains("secret-password", StringComparison.Ordinal));
        Assert.Equal("secret-password\n", Encoding.UTF8.GetString(started.StandardInput.Span));
    }

    [Fact]
    public async Task AnyConnect_accepts_a_session_only_password()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        using var password = SecretMaterial.CopyFrom("session-password"u8);
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.AnyConnect,
            vault,
            processes,
            state.Path,
            ("openconnect", "/tools/openconnect"),
            ("ocproxy", "/tools/ocproxy"));

        await using var session = Success(await transport.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test"),
                    username: "user"),
                password),
            progress: null,
            CancellationToken.None));

        var started = Assert.Single(processes.Starts);
        Assert.Contains("--passwd-on-stdin", started.Arguments, StringComparer.Ordinal);
        Assert.Equal("session-password\n", Encoding.UTF8.GetString(started.StandardInput.Span));
    }

    [Fact]
    public async Task Tailscale_uses_a_private_userspace_daemon_and_preserves_its_identity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var authKey = new SecretRef("tailscale-auth-key");
        await StoreSecretAsync(vault, authKey, "tskey-auth-secret");
        var firstProcesses = new RecordingHostVpnProcessRunner();
        firstProcesses.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        firstProcesses.CommandResults.Enqueue(new HostVpnCommandResult(
            0,
            "{\"BackendState\":\"Running\"}"));
        var firstTransport = Create(
            NetworkConnectionKind.Tailscale,
            vault,
            firstProcesses,
            state.Path,
            ("tailscaled", "/tools/tailscaled"),
            ("tailscale", "/tools/tailscale"));

        var firstSession = Success(await firstTransport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Tailscale(
                "exit-node",
                authKeySecret: authKey)),
            progress: null,
            CancellationToken.None));
        var daemon = Assert.Single(firstProcesses.Starts);
        Assert.Contains("--tun=userspace-networking", daemon.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            daemon.Arguments,
            argument => argument.StartsWith("--socks5-server=127.0.0.1:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            daemon.Arguments,
            argument => argument.StartsWith("--tun=", StringComparison.Ordinal)
                && !string.Equals(argument, "--tun=userspace-networking", StringComparison.Ordinal));
        var stateArgument = Assert.Single(
            daemon.Arguments,
            argument => argument.StartsWith("--state=", StringComparison.Ordinal));
        var statePath = stateArgument["--state=".Length..];
        await firstSession.DisposeAsync();

        Assert.True(File.Exists(statePath));
        Assert.DoesNotContain(
            firstProcesses.Commands,
            request => request.Arguments.Contains("logout", StringComparer.Ordinal));

        var secondProcesses = new RecordingHostVpnProcessRunner();
        secondProcesses.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        secondProcesses.CommandResults.Enqueue(new HostVpnCommandResult(
            0,
            "{\"BackendState\": \"Running\"}"));
        var secondTransport = Create(
            NetworkConnectionKind.Tailscale,
            vault,
            secondProcesses,
            state.Path,
            ("tailscaled", "/tools/tailscaled"),
            ("tailscale", "/tools/tailscale"));

        await using var secondSession = Success(await secondTransport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Tailscale("exit-node")),
            progress: null,
            CancellationToken.None));

        Assert.DoesNotContain(
            secondProcesses.Commands.SelectMany(request => request.Arguments),
            argument => argument.StartsWith("--auth-key=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenVpn_fails_explicitly_without_invoking_the_system_client()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.OpenVpn,
            vault,
            processes,
            state.Path,
            ("openvpn", "/tools/openvpn"));

        var result = await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.OpenVpn(new SecretRef("profile"))),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RuntimeMissing, failure.Error.Code);
        Assert.Equal("openvpn_host_userspace_adapter_missing", failure.Error.StableCode);
        Assert.Empty(processes.Starts);
        Assert.Empty(processes.Commands);
    }

    [Fact]
    public async Task Unexpected_process_exit_marks_the_session_failed()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-health-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            ("wireproxy", "/tools/wireproxy"));
        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
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

        Assert.Single(processes.Processes).Exit();
        var snapshot = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NetworkConnectionState.Failed, snapshot.State);
        Assert.Contains("stopped", snapshot.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unreachable_userspace_route_is_rejected_before_connected()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-unreachable-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner();
        var reachability = new RecordingReachabilityProbe(defaultResult: false);
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            reachability,
            null,
            ("wireproxy", "/tools/wireproxy"));

        var result = await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.Equal("wireguard_host_reachability_failed", failure.Error.StableCode);
        Assert.Equal(1, reachability.CallCount);
        Assert.True(Assert.Single(processes.Processes).HasExited);
    }

    [Fact]
    public async Task Periodic_reachability_failure_marks_userspace_session_failed()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-periodic-health-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner();
        var reachability = new RecordingReachabilityProbe(defaultResult: false);
        reachability.Results.Enqueue(true);
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            reachability,
            TimeSpan.FromMilliseconds(10),
            ("wireproxy", "/tools/wireproxy"));
        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
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

        Assert.Contains("carry", snapshot.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(reachability.CallCount >= 2);
    }

    [Fact]
    public async Task Production_reachability_probe_sends_only_literal_peers_through_Socks()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var destinations = new List<(IPAddress Address, int Port)>();
        var server = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
                await stream.WriteAsync(new byte[] { 5, 0 });
                var request = new byte[10];
                await stream.ReadExactlyAsync(request);
                Assert.Equal(5, request[0]);
                Assert.Equal(1, request[1]);
                Assert.Equal(1, request[3]);
                destinations.Add((
                    new IPAddress(request.AsSpan(4, 4)),
                    BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8))));
                await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 });
            }
        });
        var probe = new SocksReachabilityProbe();

        var reachable = await probe.ProbeAsync(
            ((IPEndPoint)listener.LocalEndpoint).Port,
            CancellationToken.None);
        await server;

        Assert.False(reachable);
        Assert.Equal(
            [(IPAddress.Parse("1.1.1.1"), 443), (IPAddress.Parse("1.0.0.1"), 443)],
            destinations);
    }

    private static HostUserspaceVpnTransport Create(
        NetworkConnectionKind kind,
        ISecretVault vault,
        IHostVpnProcessRunner processes,
        string stateRoot,
        params (string Name, string Path)[] executables) =>
        Create(
            kind,
            vault,
            processes,
            stateRoot,
            new RecordingReachabilityProbe(defaultResult: true),
            null,
            executables);

    private static HostUserspaceVpnTransport Create(
        NetworkConnectionKind kind,
        ISecretVault vault,
        IHostVpnProcessRunner processes,
        string stateRoot,
        ISocksReachabilityProbe reachabilityProbe,
        TimeSpan? healthPollInterval,
        params (string Name, string Path)[] executables) =>
        new(
            kind,
            vault,
            new DictionaryExecutableLocator(executables),
            processes,
            stateRoot,
            reachabilityProbe,
            healthPollInterval);

    private static NetworkConnectionStartRequest Request(
        NetworkConnectionConfiguration configuration,
        SecretMaterial? transientPassword = null) => new(
        WorkspaceId,
        new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Host VPN",
            configuration),
        WorkspaceNetworkPlacement.Host,
        killSwitchEnabled: false,
        transientPassword);

    private static async Task StoreSecretAsync(
        InMemorySecretVault vault,
        SecretRef reference,
        string value)
    {
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(value));
        _ = Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "Host VPN test secret",
                SecretKind.Other,
                new SecretScope(SecretScopeKind.NetworkConnection, ConnectionId.Value),
                new SecretUsePurpose(SecretUseKind.UserManagement, ConnectionId.Value)),
            material,
            CancellationToken.None));
    }

    private static T Success<T>(NetworkConnectionResult<T> result) =>
        Assert.IsType<NetworkConnectionResult<T>.Success>(result).Value;

    private sealed class DictionaryExecutableLocator(
        IEnumerable<(string Name, string Path)> executables) : IConnectionExecutableLocator
    {
        private readonly IReadOnlyDictionary<string, string> _executables =
            executables.ToDictionary(item => item.Name, item => item.Path, StringComparer.Ordinal);

        public string? Find(string executable) =>
            _executables.GetValueOrDefault(executable);
    }

    private sealed class RecordingHostVpnProcessRunner : IHostVpnProcessRunner
    {
        public List<HostVpnProcessRequest> Starts { get; } = [];

        public List<HostVpnProcessRequest> Commands { get; } = [];

        public List<RecordingHostVpnProcess> Processes { get; } = [];

        public Queue<bool> ListenerResults { get; } = [];

        public Queue<HostVpnCommandResult> CommandResults { get; } = [];

        public ValueTask<IHostVpnProcess> StartAsync(
            HostVpnProcessRequest request,
            CancellationToken cancellationToken)
        {
            Starts.Add(request with { StandardInput = request.StandardInput.ToArray() });
            var process = new RecordingHostVpnProcess();
            Processes.Add(process);
            var state = request.Arguments.FirstOrDefault(
                argument => argument.StartsWith("--state=", StringComparison.Ordinal));
            if (state is not null)
            {
                var path = state["--state=".Length..];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "private-tailscale-state");
            }

            var socket = request.Arguments.FirstOrDefault(
                argument => argument.StartsWith("--socket=", StringComparison.Ordinal));
            if (socket is not null)
            {
                File.WriteAllText(socket["--socket=".Length..], string.Empty);
            }

            return ValueTask.FromResult<IHostVpnProcess>(process);
        }

        public ValueTask<HostVpnCommandResult> RunAsync(
            HostVpnProcessRequest request,
            CancellationToken cancellationToken)
        {
            Commands.Add(request);
            return ValueTask.FromResult(CommandResults.Count == 0
                ? new HostVpnCommandResult(0, string.Empty)
                : CommandResults.Dequeue());
        }

        public ValueTask<bool> WaitForTcpListenerAsync(
            IHostVpnProcess process,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var result = ListenerResults.Count == 0 || ListenerResults.Dequeue();
            if (!result)
            {
                ((RecordingHostVpnProcess)process).Exit();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingReachabilityProbe(bool defaultResult) :
        ISocksReachabilityProbe
    {
        public Queue<bool> Results { get; } = [];

        public int CallCount { get; private set; }

        public ValueTask<bool> ProbeAsync(
            int socksPort,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(socksPort);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(
                Results.Count == 0 ? defaultResult : Results.Dequeue());
        }
    }

    private sealed class RecordingHostVpnProcess : IHostVpnProcess
    {
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited { get; private set; }

        public int? ExitCode => HasExited ? 1 : null;

        public string Diagnostic => HasExited ? "address already in use" : string.Empty;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void Exit()
        {
            HasExited = true;
            _exit.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Exit();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("ghostshell-vpn-test-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
