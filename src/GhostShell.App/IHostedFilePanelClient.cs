using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

/// <summary>
/// Exposes the hosted-session lifecycle that the compatibility file clients cannot represent.
/// </summary>
public interface IHostedFilePanelClient
{
    event EventHandler? ProfilesChanged;

    SessionId SessionId { get; }

    SessionOwner Owner { get; }

    ClientId ClientId { get; }

    bool IsInitialized { get; }

    long? Revision { get; }

    ValueTask<HostResult<SessionSnapshot>> InitializeAsync(
        CancellationToken cancellationToken);

    ValueTask<HostResult<CloseScopeResult>> CloseAsync(
        CloseDecision decision,
        CancellationToken cancellationToken);
}
