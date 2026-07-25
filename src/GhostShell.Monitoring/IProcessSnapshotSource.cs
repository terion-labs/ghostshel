namespace GhostShell.Monitoring;

internal interface IProcessSnapshotSource
{
    RawProcessCapture Capture(CancellationToken cancellationToken);
}
