using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class WorkspaceIsolationSocksProxy :
    IAsyncDisposable,
    IWorkspaceNetworkEgressSink,
    IWorkspaceNetworkConnector
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IConnectionCommandRuntime _commandRuntime;
    private readonly ConnectionProfile _connection;
    private readonly Thread _acceptThread;
    private readonly object _egressGate = new();
    private WorkspaceNetworkEgress _egress = WorkspaceNetworkEgress.Direct;
    private CancellationTokenSource _routeLifetime = new();
    private int _disposed;

    public WorkspaceIsolationSocksProxy(
        IConnectionCommandRuntime commandRuntime,
        ConnectionProfile connection)
    {
        _commandRuntime = commandRuntime ?? throw new ArgumentNullException(nameof(commandRuntime));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LocalProxyEndpoint = new Uri(
            $"socks5://127.0.0.1:{LocalPort}",
            UriKind.Absolute);
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "GhostShell workspace browser proxy",
        };
        _acceptThread.Start();
    }

    public int LocalPort { get; }

    public WorkspaceNetworkEgress Egress => CurrentRoute().Egress;

    public Uri LocalProxyEndpoint { get; }

    public ValueTask<Stream> ConnectTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken) =>
        WorkspaceSocksClient.ConnectAsync(LocalPort, host, port, cancellationToken);

    public void Apply(WorkspaceNetworkEgress egress)
    {
        ArgumentNullException.ThrowIfNull(egress);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CancellationTokenSource previous;
        lock (_egressGate)
        {
            if (_egress == egress)
            {
                return;
            }

            _egress = egress;
            previous = _routeLifetime;
            _routeLifetime = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        CancellationTokenSource routeLifetime;
        lock (_egressGate)
        {
            routeLifetime = _routeLifetime;
        }
        routeLifetime.Cancel();
        _listener.Stop();
        _acceptThread.Join();
        _lifetime.Dispose();
        routeLifetime.Dispose();
        await ValueTask.CompletedTask;
    }

    private void AcceptLoop()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                var connectionThread = new Thread(() =>
                    ServeSafely(client, _lifetime.Token))
                {
                    IsBackground = true,
                    Name = "GhostShell workspace browser connection",
                };
                connectionThread.Start();
            }
            catch (SocketException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // A failed inbound connection must not terminate the process-wide
                // proxy loop. A later browser request can open a new connection.
            }
        }
    }

    private void ServeSafely(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            ServeAsync(client, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
        }
        catch (Exception exception) when (exception is
            IOException or SocketException or ObjectDisposedException)
        {
            client.Dispose();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var route = CurrentRoute();
        using (client)
        using (var routeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   route.CancellationToken))
        {
            cancellationToken = routeCancellation.Token;
            var stream = client.GetStream();
            var greeting = new byte[2];
            if (!await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false)
                || greeting[0] != 5)
            {
                return;
            }

            var methods = new byte[greeting[1]];
            if (!await ReadExactlyAsync(stream, methods, cancellationToken).ConfigureAwait(false)
                || !methods.Contains((byte)0))
            {
                await stream.WriteAsync(new byte[] { 5, 255 }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken)
                .ConfigureAwait(false);
            var request = new byte[4];
            if (!await ReadExactlyAsync(stream, request, cancellationToken).ConfigureAwait(false)
                || request[0] != 5
                || request[1] != 1)
            {
                return;
            }

            var host = await ReadHostAsync(stream, request[3], cancellationToken)
                .ConfigureAwait(false);
            var portBytes = new byte[2];
            if (host is null
                || !await ReadExactlyAsync(stream, portBytes, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
            var egress = route.Egress;
            if (egress == WorkspaceNetworkEgress.Blocked)
            {
                await ReplyAsync(stream, 2, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (egress.ProxyEndpoint is { } proxyEndpoint)
            {
                await ServeThroughProxyAsync(
                        client,
                        stream,
                        proxyEndpoint,
                        host,
                        port,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var process = await StartRelayAsync(host, port, cancellationToken)
                .ConfigureAwait(false);
            if (process is null)
            {
                await ReplyAsync(stream, 1, cancellationToken).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await ReplyAsync(stream, 0, cancellationToken).ConfigureAwait(false);
            using var cancellation = cancellationToken.Register(() =>
            {
                client.Dispose();
                TryKill(process);
            });
            var upstream = new Thread(() => Copy(stream, process.StandardInput.BaseStream))
            {
                IsBackground = true,
                Name = "GhostShell workspace browser upload",
            };
            var downstream = new Thread(() => Copy(process.StandardOutput.BaseStream, stream))
            {
                IsBackground = true,
                Name = "GhostShell workspace browser download",
            };
            upstream.Start();
            downstream.Start();
            upstream.Join();
            process.StandardInput.Close();
            downstream.Join();
            TryKill(process);
            await stream.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
        }
    }

    private (WorkspaceNetworkEgress Egress, CancellationToken CancellationToken) CurrentRoute()
    {
        lock (_egressGate)
        {
            return (_egress, _routeLifetime.Token);
        }
    }

    private static async Task ServeThroughProxyAsync(
        TcpClient client,
        Stream downstream,
        Uri proxyEndpoint,
        string host,
        ushort port,
        CancellationToken cancellationToken)
    {
        using var upstreamClient = new TcpClient { NoDelay = true };
        var successReplyStarted = false;
        try
        {
            await upstreamClient.ConnectAsync(
                    proxyEndpoint.Host,
                    proxyEndpoint.Port,
                    cancellationToken)
                .ConfigureAwait(false);
            var upstream = upstreamClient.GetStream();
            await ConnectSocksAsync(upstream, host, port, cancellationToken)
                .ConfigureAwait(false);
            successReplyStarted = true;
            await ReplyAsync(downstream, 0, cancellationToken).ConfigureAwait(false);
            using var cancellation = cancellationToken.Register(() =>
            {
                client.Dispose();
                upstreamClient.Dispose();
            });
            var upload = new Thread(() => Copy(downstream, upstream))
            {
                IsBackground = true,
                Name = "GhostShell workspace proxy upload",
            };
            var download = new Thread(() => Copy(upstream, downstream))
            {
                IsBackground = true,
                Name = "GhostShell workspace proxy download",
            };
            upload.Start();
            download.Start();
            upload.Join();
            upstreamClient.Client.Shutdown(SocketShutdown.Send);
            download.Join();
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            if (!successReplyStarted)
            {
                await ReplyAsync(downstream, 1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask ConnectSocksAsync(
        Stream stream,
        string host,
        ushort port,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken)
            .ConfigureAwait(false);
        var greeting = new byte[2];
        if (!await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false)
            || greeting[0] != 5
            || greeting[1] != 0)
        {
            throw new IOException("The workspace proxy rejected the connection.");
        }

        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length is 0 or > 255)
        {
            throw new IOException("The destination host is too long for SOCKS5.");
        }

        var request = new byte[7 + hostBytes.Length];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = 3;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(5 + hostBytes.Length), port);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new byte[4];
        if (!await ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false)
            || response[0] != 5
            || response[1] != 0)
        {
            throw new IOException("The workspace proxy could not reach the destination.");
        }

        var addressLength = response[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };
        if (addressLength <= 0)
        {
            throw new IOException("The workspace proxy returned an invalid response.");
        }

        var remainder = new byte[addressLength + 2];
        if (!await ReadExactlyAsync(stream, remainder, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The workspace proxy closed the connection.");
        }
    }

    private static void Copy(Stream source, Stream destination)
    {
        try
        {
            source.CopyTo(destination);
            destination.Flush();
        }
        catch (Exception exception) when (exception is
            IOException or ObjectDisposedException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async ValueTask<Process?> StartRelayAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var planned = await _commandRuntime.PlanDuplexCommandAsync(
            _connection,
            "/bin/sh",
            [
                "-c",
                "if command -v nc >/dev/null 2>&1; then exec nc \"$1\" \"$2\"; elif [ -x /bin/bash ]; then exec /bin/bash -c 'exec 3<>\"/dev/tcp/$1/$2\"; cat <&3 & cat >&3; wait' ghostshell-tcp \"$1\" \"$2\"; else exit 127; fi",
                "ghostshell-tcp",
                host,
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ],
            cancellationToken).ConfigureAwait(false);
        if (planned is not ConnectionRuntimeResult<TerminalLaunchRequest>.Success success)
        {
            return null;
        }

        var start = new ProcessStartInfo
        {
            FileName = success.Value.Executable
                ?? throw new InvalidOperationException("The relay plan has no executable."),
            WorkingDirectory = success.Value.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in success.Value.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in success.Value.Environment)
        {
            start.Environment[name] = value;
        }

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception)
        {
            process.Dispose();
            return null;
        }
    }

    private static async ValueTask<string?> ReadHostAsync(
        Stream stream,
        byte addressType,
        CancellationToken cancellationToken)
    {
        var length = addressType switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };
        if (length <= 0)
        {
            return null;
        }

        var bytes = new byte[length];
        if (!await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return addressType == 3
            ? Encoding.ASCII.GetString(bytes)
            : new IPAddress(bytes).ToString();
    }

    private static async ValueTask<int> ReadDomainLengthAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var length = new byte[1];
        return await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false)
            ? length[0]
            : 0;
    }

    private static ValueTask ReplyAsync(
        Stream stream,
        byte status,
        CancellationToken cancellationToken) =>
        stream.WriteAsync(
            new byte[] { 5, status, 0, 1, 0, 0, 0, 0, 0, 0 },
            cancellationToken);

    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
