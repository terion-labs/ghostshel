using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

/// <summary>
/// Creates the single cross-platform terminal engine used by GhostSHELL.
/// Porta.Pty owns the process transport; libghostty-vt owns terminal semantics.
/// </summary>
public sealed class GhosttyVtTerminalSessionFactory : ITerminalSessionFactory
{
    private const int InitialColumns = 80;
    private const int InitialRows = 24;
    private readonly IPortablePtyFactory _ptyFactory;
    private readonly GhosttyShellIntegrationLaunchAdapter _shellIntegration;
    private readonly ClaudeCodeTerminalLaunchAdapter _claudeCodeIntegration;

    public GhosttyVtTerminalSessionFactory()
        : this(
            new PortaPtyFactory(),
            new GhosttyShellIntegrationLaunchAdapter(),
            new ClaudeCodeTerminalLaunchAdapter())
    {
    }

    internal GhosttyVtTerminalSessionFactory(IPortablePtyFactory ptyFactory)
        : this(
            ptyFactory,
            new GhosttyShellIntegrationLaunchAdapter(),
            new ClaudeCodeTerminalLaunchAdapter())
    {
    }

    internal GhosttyVtTerminalSessionFactory(
        IPortablePtyFactory ptyFactory,
        GhosttyShellIntegrationLaunchAdapter shellIntegration)
        : this(ptyFactory, shellIntegration, new ClaudeCodeTerminalLaunchAdapter())
    {
    }

    internal GhosttyVtTerminalSessionFactory(
        IPortablePtyFactory ptyFactory,
        GhosttyShellIntegrationLaunchAdapter shellIntegration,
        ClaudeCodeTerminalLaunchAdapter claudeCodeIntegration)
    {
        _ptyFactory = ptyFactory ?? throw new ArgumentNullException(nameof(ptyFactory));
        _shellIntegration = shellIntegration
            ?? throw new ArgumentNullException(nameof(shellIntegration));
        _claudeCodeIntegration = claudeCodeIntegration
            ?? throw new ArgumentNullException(nameof(claudeCodeIntegration));
    }

    public CapabilitySet Capabilities => GhosttyVtTerminalSession.SessionCapabilities;

    public async ValueTask<ITerminalPanelSession> CreateAsync(
        SessionId sessionId,
        TerminalLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var availability = GhosttyVt.GhosttyVtRuntimeProbe.Detect();
        if (!availability.IsAvailable)
        {
            throw new PlatformNotSupportedException(availability.Detail);
        }

        // Shell scripts affect only the child process. The session retains the
        // original launch as its durable connection and recovery identity.
        var shellIntegration = _shellIntegration.Prepare(launch);
        var processLaunch = _claudeCodeIntegration.Prepare(shellIntegration);
        var pty = await _ptyFactory.SpawnAsync(
                processLaunch,
                InitialColumns,
                InitialRows,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new GhosttyVtTerminalSession(
                sessionId,
                launch,
                pty,
                InitialColumns,
                InitialRows,
                shellIntegration.IsApplied);
        }
        catch
        {
            pty.Dispose();
            throw;
        }
    }
}
