using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceIsolationSelectedRouteTests
{
    [Fact]
    public async Task BrowserProxyPlansItsRelayThroughTheSelectedConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = SshConnection();
        var runtime = new RecordingCommandRuntime();
        await using var proxy = new WorkspaceIsolationSocksProxy(runtime, connection);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort, timeout.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, timeout.Token);
        var host = Encoding.ASCII.GetBytes("example.com");
        byte[] request = [5, 1, 0, 3, checked((byte)host.Length), .. host, 0, 80];
        await stream.WriteAsync(request, timeout.Token);

        Assert.Equal(connection.Id, await runtime.PlannedConnection.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task DatabaseTunnelPlansItsRelayThroughTheSelectedConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = SshConnection();
        var runtime = new RecordingCommandRuntime();
        var factory = new WorkspaceIsolationTcpTunnelFactory(runtime);
        await using var tunnel = await factory.OpenAsync(
            connection,
            "database.internal",
            5432,
            timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, timeout.Token);

        Assert.Equal(connection.Id, await runtime.PlannedConnection.WaitAsync(timeout.Token));
    }

    private static ConnectionProfile SshConnection() => new(
        new ConnectionId("isolated-route-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "External server",
        new ConnectionEndpoint.Ssh("example.com", username: "tester"),
        new ConnectionAuthentication.None(),
        new ConnectionStartup(),
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.InsecureIgnore);

    private sealed class RecordingCommandRuntime : IConnectionCommandRuntime
    {
        private readonly TaskCompletionSource<ConnectionId> _plannedConnection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ConnectionId> PlannedConnection => _plannedConnection.Task;

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            _ = executable;
            _ = arguments;
            cancellationToken.ThrowIfCancellationRequested();
            _plannedConnection.TrySetResult(connection.Id);
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.ProcessFailed,
                    "test.relay-stopped",
                    "The test stopped before launching a relay.",
                    Retryable: false,
                    ConnectionRecoveryAction.None)));
        }

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            PlanCommandAsync(connection, executable, arguments, cancellationToken);
    }
}
