namespace GhostShell.Application;

public sealed record CapabilityNegotiation(
    CapabilitySet Client,
    CapabilitySet Host,
    CapabilitySet Engine,
    CapabilitySet Session,
    CapabilitySet Effective);
