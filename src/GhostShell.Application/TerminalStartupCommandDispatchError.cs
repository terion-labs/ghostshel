namespace GhostShell.Application;

public sealed record TerminalStartupCommandDispatchError(
    TerminalStartupCommandDispatchErrorCode Code,
    string Message,
    bool Retryable);
