using GhostShell.Core;

namespace GhostShell.Application;

public interface ITerminalSessionFactory
{
    CapabilitySet Capabilities { get; }

    ValueTask<ITerminalPanelSession> CreateAsync(
        SessionId sessionId,
        TerminalLaunchRequest launch,
        CancellationToken cancellationToken);
}
