using GhostShell.Application;

namespace GhostShell.App;

/// <summary>
/// Separates the durable import result from presentation reload status. A reload failure never
/// misreports an already-committed import as rolled back.
/// </summary>
public sealed record DefinitionBundleImportReceipt(
    int Inserted,
    int Replaced,
    DefinitionStoreError? ReloadError)
{
    public bool CatalogReloaded => ReloadError is null;
}
