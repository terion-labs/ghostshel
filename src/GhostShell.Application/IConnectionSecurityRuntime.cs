using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Keeps host-key bytes and resolved authentication material behind Infrastructure while exposing
/// non-secret review and diagnostics projections to desktop and future headless clients.
/// </summary>
public interface IConnectionSecurityRuntime
{
    ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
        SshHostKeyTrustRequest request,
        CancellationToken cancellationToken);

    ValueTask<ConnectionRuntimeResult<ConnectionDiagnosticsReport>> DiagnoseAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);
}
