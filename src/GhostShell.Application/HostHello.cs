namespace GhostShell.Application;

public sealed record HostHello(
    int ProtocolVersion,
    HostMode HostMode,
    CapabilitySet Capabilities);
