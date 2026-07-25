using GhostShell.Core;

namespace GhostShell.Application;

public sealed record SecretMetadata(
    SecretRef Reference,
    string Label,
    SecretKind Kind,
    SecretScope Scope,
    SecretVaultPersistenceKind Persistence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt = null);
