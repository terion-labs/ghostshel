using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Converts a connection's prepared terminal launch into a one-shot command.
/// This deliberately reuses the connection runtime so credential brokerage and
/// private host-key bindings are identical to interactive terminal sessions.
/// </summary>
public sealed class ConnectionCommandExecutor(IConnectionRuntime connectionRuntime)
    : IConnectionCommandExecutor
{
    private const int SshControlPersistSeconds = 15;
    private static readonly string SshControlInstance =
        RandomNumberGenerator.GetHexString(4, lowercase: true);

    public async ValueTask<ConnectionCommandResult> ExecuteAsync(
        ConnectionCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        var planResult = await connectionRuntime
            .PlanOpenAsync(request.Connection, progress: null, cancellationToken)
            .ConfigureAwait(false);
        if (planResult is not ConnectionRuntimeResult<ConnectionOpenPlan>.Success success)
        {
            return new ConnectionCommandResult(
                ConnectionCommandOutcome.ConnectionFailed,
                null,
                string.Empty);
        }

        var start = CreateStartInfo(success.Value.Launch, request);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return StartFailed();
            }
        }
        catch (Exception exception) when (exception is
            Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            return StartFailed();
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var stdout = ReadBoundedAsync(
            process.StandardOutput,
            request.MaximumOutputCharacters,
            linked.Token);
        var stderr = DrainAsync(process.StandardError, linked.Token);
        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(linked.Token),
                    stdout,
                    stderr)
                .ConfigureAwait(false);
            return new ConnectionCommandResult(
                ConnectionCommandOutcome.Exited,
                process.ExitCode,
                await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainAsync(stdout, stderr).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : new ConnectionCommandResult(
                    ConnectionCommandOutcome.TimedOut,
                    null,
                    string.Empty);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        TerminalLaunchRequest launch,
        ConnectionCommand request)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.Connection.ConnectionKind == ConnectionKind.Local
                ? request.Executable
                : launch.Executable
                    ?? throw new InvalidOperationException("The connection plan has no executable."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in CommandArguments(launch, request))
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            start.Environment[name] = value;
        }

        return start;
    }

    private static IReadOnlyList<string> CommandArguments(
        TerminalLaunchRequest launch,
        ConnectionCommand request) =>
        request.Connection.ConnectionKind switch
        {
            ConnectionKind.Local =>
                request.Arguments,
            ConnectionKind.Ssh =>
                SshArguments(
                    launch.Arguments,
                    request,
                    SshControlPath(request.Connection)),
            ConnectionKind.Docker =>
                DockerArguments(launch.Arguments, request),
            ConnectionKind.Wsl =>
                [.. launch.Arguments, "--exec", request.Executable, .. request.Arguments],
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Connection.ConnectionKind,
                "The connection kind cannot execute structured commands."),
        };

    internal static IReadOnlyList<string> SshArguments(
        IReadOnlyList<string> launchArguments,
        ConnectionCommand request,
        string? controlPath)
    {
        var boundary = -1;
        for (var index = 0; index < launchArguments.Count; index++)
        {
            if (launchArguments[index] == "--")
            {
                boundary = index;
                break;
            }
        }
        if (boundary < 0 || boundary + 1 >= launchArguments.Count)
        {
            throw new InvalidOperationException("The SSH connection plan is malformed.");
        }

        var arguments = launchArguments
            .Take(boundary)
            .Where(argument => argument != "-tt")
            .ToList();
        if (controlPath is not null)
        {
            // A monitor samples every two seconds. OpenSSH multiplexing keeps those bounded
            // commands on one authenticated transport while preserving process isolation and
            // automatic reconnection when the master connection disappears.
            arguments.AddRange(
            [
                "-o",
                "ControlMaster=auto",
                "-o",
                $"ControlPersist={SshControlPersistSeconds}",
                "-o",
                $"ControlPath={controlPath}",
            ]);
        }

        arguments.Add(launchArguments[boundary]);
        arguments.Add(launchArguments[boundary + 1]);
        var command = new[] { request.Executable }
            .Concat(request.Arguments)
            .Select(QuotePosixShellWord);
        arguments.Add(string.Join(' ', command));
        return Array.AsReadOnly(arguments.ToArray());
    }

    internal static string? SshControlPath(ConnectionProfile connection)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        var identity = new StringBuilder(connection.Id.Value)
            .Append('\0')
            .Append(connection.HostKeyPolicy);
        if (connection.Endpoint is ConnectionEndpoint.Ssh endpoint)
        {
            identity
                .Append('\0')
                .Append(endpoint.Host)
                .Append('\0')
                .Append(endpoint.Port)
                .Append('\0')
                .Append(endpoint.Username);
        }

        AppendAuthenticationIdentity(identity, connection.Authentication);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
        var profileIdentity = Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
        return $"/tmp/ghostshell-{Environment.ProcessId}-{SshControlInstance}-{profileIdentity}-%C";
    }

    private static void AppendAuthenticationIdentity(
        StringBuilder identity,
        ConnectionAuthentication authentication)
    {
        identity.Append('\0').Append(authentication.GetType().Name);
        switch (authentication)
        {
            case ConnectionAuthentication.Password password:
                identity.Append('\0').Append(password.PasswordSecret.Value);
                break;
            case ConnectionAuthentication.PrivateKey privateKey:
                identity
                    .Append('\0')
                    .Append(privateKey.PrivateKeySecret.Value)
                    .Append('\0')
                    .Append(privateKey.PassphraseSecret?.Value);
                break;
        }
    }

    private static IReadOnlyList<string> DockerArguments(
        IReadOnlyList<string> launchArguments,
        ConnectionCommand request)
    {
        var arguments = launchArguments
            .Where(argument => argument is not "--interactive" and not "--tty")
            .ToList();
        if (arguments.Count == 0 || arguments[^1] != "/bin/sh")
        {
            throw new InvalidOperationException("The Docker connection plan is malformed.");
        }

        arguments.RemoveAt(arguments.Count - 1);
        arguments.Add(request.Executable);
        arguments.AddRange(request.Arguments);
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static string QuotePosixShellWord(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var result = new char[maximumCharacters];
        var scratch = new char[4096];
        var written = 0;
        while (true)
        {
            var read = await reader.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new string(result, 0, written);
            }

            var copy = Math.Min(read, maximumCharacters - written);
            if (copy > 0)
            {
                scratch.AsSpan(0, copy).CopyTo(result.AsSpan(written));
                written += copy;
            }
        }
    }

    private static async Task DrainAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    private static async Task AwaitDrainAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static ConnectionCommandResult StartFailed() =>
        new(ConnectionCommandOutcome.StartFailed, null, string.Empty);

    private static ConnectionCommandResult Cancelled() =>
        new(ConnectionCommandOutcome.Cancelled, null, string.Empty);
}
