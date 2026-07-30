namespace GhostShell.Monitoring;

internal interface IProcessSnapshotSource
{
    ValueTask<RawProcessCapture> CaptureAsync(CancellationToken cancellationToken);
}
