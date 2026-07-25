using GhostShell.Core;

namespace GhostShell.Application;

public sealed record DeleteSecretRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
