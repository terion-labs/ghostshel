using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class WorkspaceIsolationSocksProxy : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IConnectionCommandRuntime _commandRuntime;
    private readonly ConnectionProfile _connection;
    private readonly Thread _acceptThread;
    private int _disposed;

    public WorkspaceIsolationSocksProxy(
        IConnectionCommandRuntime commandRuntime,
        ConnectionProfile connection)
    {
        _commandRuntime = commandRuntime ?? throw new ArgumentNullException(nameof(commandRuntime));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "GhostShell workspace browser proxy",
        };
        _acceptThread.Start();
    }

    public int LocalPort { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _listener.Stop();
        _acceptThread.Join();
        _lifetime.Dispose();
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        using (client)
        {
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
