using GhostShell.Application;

namespace GhostShell.Infrastructure;

internal static class SecretVaultAuthorization
{
    public static SecretVaultResult<T>? Authorize<T>(
        ISecretAccessPolicy policy,
        SecretVaultOperation operation,
        SecretScope? scope,
        SecretUsePurpose purpose) =>
        policy.IsAllowed(operation, scope, purpose)
            ? null
            : SecretVaultFailures.AccessDenied<T>();

    public static SecretVaultResult<T>? MatchStoredScope<T>(
        SecretScope expected,
        SecretScope stored) =>
        expected == stored
            ? null
            : SecretVaultFailures.AccessDenied<T>();
}
