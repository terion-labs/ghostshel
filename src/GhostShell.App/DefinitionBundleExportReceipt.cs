namespace GhostShell.App;

public sealed record DefinitionBundleExportReceipt(
    string Path,
    int DefinitionCount,
    DateTimeOffset ExportedAt);
