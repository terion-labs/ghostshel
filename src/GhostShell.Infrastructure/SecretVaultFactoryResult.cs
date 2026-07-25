using GhostShell.Application;

namespace GhostShell.Infrastructure;

public sealed record SecretVaultFactoryResult(
    ISecretVault Vault,
    SecretVaultFactoryDiagnostic Diagnostic);
