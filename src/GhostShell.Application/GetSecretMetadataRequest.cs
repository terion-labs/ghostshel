using GhostShell.Core;

namespace GhostShell.Application;

public sealed record GetSecretMetadataRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
