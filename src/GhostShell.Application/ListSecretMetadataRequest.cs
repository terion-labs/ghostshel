namespace GhostShell.Application;

public sealed record ListSecretMetadataRequest(SecretScope? Scope, SecretUsePurpose Purpose);
