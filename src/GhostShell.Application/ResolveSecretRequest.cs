using GhostShell.Core;

namespace GhostShell.Application;

public sealed record ResolveSecretRequest(
    SecretRef Reference,
    SecretScope Scope,
    SecretUsePurpose Purpose);
