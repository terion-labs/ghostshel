namespace GhostShell.Application;

public enum SessionCloseOutcome
{
    GracefullyClosed,
    ConfirmationRequired,
    Cancelled,
    ForceTerminated,
    EngineFailed,
    AlreadyClosed,
}
