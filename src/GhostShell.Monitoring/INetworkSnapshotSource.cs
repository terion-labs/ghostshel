namespace GhostShell.Monitoring;

internal interface INetworkSnapshotSource
{
    ValueTask<IReadOnlyList<RawNetworkObservation>> CaptureAsync(
        CancellationToken cancellationToken);
}
