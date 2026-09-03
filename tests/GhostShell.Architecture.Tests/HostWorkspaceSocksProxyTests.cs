using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class HostWorkspaceSocksProxyTests
{
    [Fact]
    public async Task Direct_route_reaches_the_destination()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var echo = EchoOnceAsync(destination);
        await using var proxy = new HostWorkspaceSocksProxy();
        using var client = await OpenAsync(proxy.LocalPort, "127.0.0.1", destinationPort);

        await client.GetStream().WriteAsync("ping"u8.ToArray());
        var reply = new byte[4];
        await ReadRequiredAsync(client.GetStream(), reply);

        Assert.Equal("pong", Encoding.ASCII.GetString(reply));
        await echo;
    }

    [Fact]
    public async Task Proxy_route_chains_through_the_selected_adapter()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var observed = ObserveSocksDestinationAsync(upstream);
        await using var proxy = new HostWorkspaceSocksProxy();
        proxy.Apply(WorkspaceNetworkEgress.ViaProxy(
            new Uri($"socks5://127.0.0.1:{upstreamPort}")));

        using var client = await OpenAsync(proxy.LocalPort, "service.example", 9443);

        Assert.Equal(("service.example", 9443), await observed);
    }

    [Fact]
    public async Task Blocked_route_rejects_new_connections()
    {
        await using var proxy = new HostWorkspaceSocksProxy();
        proxy.Apply(WorkspaceNetworkEgress.Blocked);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var greeting = new byte[2];
        await ReadRequiredAsync(stream, greeting);
        await stream.WriteAsync(new byte[] { 5, 1, 0, 3, 1, (byte)'x', 0, 80 });
        var response = new byte[10];
        await ReadRequiredAsync(stream, response);

        Assert.Equal((byte)2, response[1]);
    }

    [Fact]
    public async Task Database_relay_opens_its_real_socket_through_workspace_connector()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        var echo = EchoOnceAsync(destination);
        await using var proxy = new HostWorkspaceSocksProxy();
        var tunnels = new WorkspaceNetworkDatabaseTunnelFactory(
            proxy,
            new RejectingSshTunnelFactory());
        await using var tunnel = await tunnels.OpenAsync(
            BuiltInConnections.Local,
            "127.0.0.1",
            destinationPort,
            CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort);

        await client.GetStream().WriteAsync("ping"u8.ToArray());
        var reply = new byte[4];
        await ReadRequiredAsync(client.GetStream(), reply);

        Assert.Equal("pong", Encoding.ASCII.GetString(reply));
        client.Dispose();
        await echo;
    }

    private static async Task<TcpClient> OpenAsync(
        int proxyPort,
        string host,
        int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
        var greeting = new byte[2];
        await ReadRequiredAsync(stream, greeting, timeout.Token);
        Assert.Equal(new byte[] { 5, 0 }, greeting);
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = 3;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        BinaryPrimitives.WriteUInt16BigEndian(
            request.AsSpan(5 + hostBytes.Length),
            checked((ushort)port));
        await stream.WriteAsync(request, timeout.Token);
        var response = new byte[10];
        await ReadRequiredAsync(stream, response, timeout.Token);
        Assert.Equal((byte)0, response[1]);
        return client;
    }

    private static async Task EchoOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var request = new byte[4];
        await ReadRequiredAsync(client.GetStream(), request);
        Assert.Equal("ping", Encoding.ASCII.GetString(request));
        await client.GetStream().WriteAsync("pong"u8.ToArray());
    }

    private static async Task<(string Host, int Port)> ObserveSocksDestinationAsync(
        TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var greeting = new byte[3];
        await ReadRequiredAsync(stream, greeting);
        await stream.WriteAsync(new byte[] { 5, 0 });
        var request = new byte[5];
        await ReadRequiredAsync(stream, request);
        var host = new byte[request[4]];
        await ReadRequiredAsync(stream, host);
        var port = new byte[2];
        await ReadRequiredAsync(stream, port);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        return (Encoding.ASCII.GetString(host), BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    private static Task ReadRequiredAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        stream.ReadExactlyAsync(buffer, cancellationToken).AsTask();

    private sealed class RejectingSshTunnelFactory : IDatabaseTunnelFactory
    {
        public ValueTask<IDatabaseTunnelLease> OpenAsync(
            ConnectionProfile connection,
            string targetHost,
            int targetPort,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IDatabaseTunnelLease>(
                new InvalidOperationException("The test does not use an SSH tunnel."));
    }
}
