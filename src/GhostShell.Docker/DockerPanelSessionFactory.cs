using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Docker;

/// <summary>
/// Opens one read-bounded Docker engine and proves connectivity before the
/// session is admitted to SessionHost.
/// </summary>
public sealed class DockerPanelSessionFactory(
    IDockerEngineClient client,
    TimeProvider timeProvider) : IDockerPanelSessionFactory
{
    private readonly IDockerEngineClient _client =
        client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.DockerReadState,
        SessionCapabilities.DockerInspect,
        SessionCapabilities.DockerReadLogs,
        SessionCapabilities.DockerFilesList,
        SessionCapabilities.DockerFilesStat,
        SessionCapabilities.DockerFilesRead,
    ]);

    public async ValueTask<IDockerPanelSession> CreateAsync(
        SessionId sessionId,
        DockerSessionTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var opened = await _client
            .ReadSnapshotAsync(target.Connection, cancellationToken)
            .ConfigureAwait(false);
        if (opened is not DockerResult<DockerEngineSnapshot>.Success success)
        {
            // Provider error text can include daemon endpoints or host paths.
            // SessionHost maps this fixed failure without retaining the source.
            throw new InvalidOperationException(
                "The Docker engine could not be opened.");
        }

        return new DockerPanelSession(
            sessionId,
            target,
            _client,
            success.Value,
            Capabilities,
            _timeProvider);
    }
}
