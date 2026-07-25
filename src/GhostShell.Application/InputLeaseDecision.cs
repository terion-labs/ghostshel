namespace GhostShell.Application;

public sealed record InputLeaseDecision(
    bool Granted,
    InputLease? Lease,
    string Detail,
    bool PreemptedAnotherHolder = false);
