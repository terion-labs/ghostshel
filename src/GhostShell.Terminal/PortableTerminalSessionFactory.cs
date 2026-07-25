using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

public sealed class PortableTerminalSessionFactory : ITerminalSessionFactory
{
    private const int InitialColumns = 80;
    private const int InitialRows = 24;
    private readonly IPortablePtyFactory _ptyFactory;

    public PortableTerminalSessionFactory()
        : this(new PortaPtyFactory())
    {
    }

    internal PortableTerminalSessionFactory(IPortablePtyFactory ptyFactory)
    {
        _ptyFactory = ptyFactory ?? throw new ArgumentNullException(nameof(ptyFactory));
    }

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.ManagedRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalScrollback,
        SessionCapabilities.TerminalClearScrollback,
        SessionCapabilities.TerminalFind,
        SessionCapabilities.TerminalSelection,
        SessionCapabilities.TerminalPaste,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalFocus,
    ]);

    public async ValueTask<ITerminalPanelSession> CreateAsync(
        SessionId sessionId,
        TerminalLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var pty = await _ptyFactory.SpawnAsync(
                launch,
                InitialColumns,
                InitialRows,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new PortableTerminalSession(sessionId, launch, pty, InitialColumns, InitialRows);
        }
        catch
        {
            pty.Dispose();
            throw;
        }
    }
}
