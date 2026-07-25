using GhostShell.Application;

namespace GhostShell.App.Controls;

public sealed class TerminalSessionFailureEventArgs(SessionFailure failure) : EventArgs
{
    public SessionFailure Failure { get; } = failure ?? throw new ArgumentNullException(nameof(failure));
}
