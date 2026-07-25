using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

public sealed class GhosttyTerminalSessionFactory : ITerminalSessionFactory
{
    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.NativeRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalPaste,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalFocus,
    ]);

    public ValueTask<ITerminalPanelSession> CreateAsync(
        SessionId sessionId,
        TerminalLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        cancellationToken.ThrowIfCancellationRequested();
        ITerminalPanelSession session = new GhosttyTerminalSession(sessionId, launch);
        return ValueTask.FromResult(session);
    }
}
