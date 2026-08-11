namespace GhostShell.Monitoring;

internal sealed record RawNetworkObservation(
    string InterfaceId,
    long ReceivedBytes,
    long SentBytes);
