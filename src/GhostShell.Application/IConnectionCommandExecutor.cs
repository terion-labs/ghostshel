using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Executes one bounded, non-interactive command on a connection target.
/// Callers provide argv as data; connection adapters own authentication,
/// host verification, and the platform-specific process invocation.
/// </summary>
public interface IConnectionCommandExecutor
{
    ValueTask<ConnectionCommandResult> ExecuteAsync(
        ConnectionCommand request,
        CancellationToken cancellationToken);
}

public sealed record ConnectionCommand
{
    public ConnectionCommand(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumOutputCharacters)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maximumOutputCharacters is <= 0 or > 2 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        }

        if (executable.Contains('\0')
            || arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new ArgumentException("Command argv cannot contain NUL characters.");
        }

        Executable = executable;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Timeout = timeout;
        MaximumOutputCharacters = maximumOutputCharacters;
    }

    public ConnectionProfile Connection { get; }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }

    public int MaximumOutputCharacters { get; }
}

public enum ConnectionCommandOutcome
{
    Exited,
    StartFailed,
    TimedOut,
    Cancelled,
    ConnectionFailed,
}

public sealed record ConnectionCommandResult(
    ConnectionCommandOutcome Outcome,
    int? ExitCode,
    string StandardOutput);
