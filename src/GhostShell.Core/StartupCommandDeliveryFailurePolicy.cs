namespace GhostShell.Core;

public enum StartupCommandDeliveryFailurePolicy
{
    RetryWhileLive,
    StopAfterFirstDeliveryFailure,
}
