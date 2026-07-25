using GhostShell.Core;

namespace GhostShell.Application;

public sealed record ReplaceSecretRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
