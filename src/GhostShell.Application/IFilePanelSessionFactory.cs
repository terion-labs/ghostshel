using GhostShell.Core;

namespace GhostShell.Application;

public interface IFilePanelSessionFactory
{
    CapabilitySet Capabilities { get; }

    ValueTask<IFilePanelSession> CreateAsync(
        SessionId sessionId,
        FilePanelLocation initialLocation,
        CancellationToken cancellationToken);
}
