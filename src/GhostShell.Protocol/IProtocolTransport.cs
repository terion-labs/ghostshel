using System.Text.Json;

namespace GhostShell.Protocol;

public interface IProtocolTransport
{
    ValueTask<ProtocolResponseEnvelope<JsonElement>> SendAsync(
        ProtocolRequestEnvelope<JsonElement> request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProtocolSessionEventEnvelope> WatchAsync(
        ProtocolWatchRequest request,
        CancellationToken cancellationToken);
}
