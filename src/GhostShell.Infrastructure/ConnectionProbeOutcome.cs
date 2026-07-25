namespace GhostShell.Infrastructure;

public enum ConnectionProbeOutcome
{
    Exited,
    TimedOut,
    Cancelled,
    StartFailed,
}
