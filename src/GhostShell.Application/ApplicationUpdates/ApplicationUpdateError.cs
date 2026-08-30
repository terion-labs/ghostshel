namespace GhostShell.Application.ApplicationUpdates;

public enum ApplicationUpdateError
{
    None,
    InvalidDistributionIdentity,
    NotInstalledByVelopack,
    CheckFailed,
    DownloadFailed,
    ApplyFailed,
}
