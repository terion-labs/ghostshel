namespace GhostShell.Application;

public sealed record ClientHello(
    IReadOnlyList<int> SupportedProtocolVersions,
    CapabilitySet Capabilities);
