namespace GhostShell.Application;

public sealed record DiagnosticsBundleReceipt(
    int ArtifactCount,
    long TotalArtifactBytes,
    long ArchiveBytes,
    string Sha256);
