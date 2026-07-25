using GhostShell.Application;

namespace GhostShell.Infrastructure;

public sealed record SecretVaultFactoryDiagnostic(
    SecretVaultPlatform Platform,
    string Adapter,
    string StableCode,
    string Message,
    SecretVaultAvailability Availability);
