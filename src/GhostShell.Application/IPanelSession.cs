using GhostShell.Core;

namespace GhostShell.Application;

public interface IPanelSession : IAsyncDisposable
{
    SessionId Id { get; }

    PanelKind Kind { get; }

    CapabilitySet Capabilities { get; }

    ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        CancellationToken cancellationToken);

    ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken);
}
