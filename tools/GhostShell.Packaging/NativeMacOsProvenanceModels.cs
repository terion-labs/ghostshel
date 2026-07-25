namespace GhostShell.Packaging;

internal static class NativeMacOsProvenanceSchema
{
    public const int Version = 1;
    public const string Generator = "GhostShell.Packaging-1.0.0";
    public const string ReceiptFileName = "native-macos-build-receipt.json";
    public const string CatalogFileName = "native-macos-components.json";
    public const string BuildEvidenceFileName =
        "macos-arm64-build-evidence.json";
    public const string ResourceEvidenceFileName =
        "macos-arm64-resource-evidence.json";
    public const string LegalConclusion = "NOT_ASSERTED";
    public const string ReleaseReadiness = "BLOCKED";

    public const int MaximumJsonDepth = 24;
    public const int MaximumInputs = 256;
    public const int MaximumComponents = 256;
    public const int MaximumBlockers = 256;
    public const int MaximumArtifactFiles = 20_000;
    public const int MaximumArtifactEntries = 40_000;
    public const int MaximumPathCharacters = 240;
    public const int MaximumPathSegments = 32;
    public const long MaximumArtifactBytes = 8L * 1024 * 1024 * 1024;
    public const long MaximumCatalogBytes = 2L * 1024 * 1024;
    public const long MaximumReceiptBytes = 8L * 1024 * 1024;
}

internal sealed record NativeMacOsTarget(
    string OperatingSystem,
    string Architecture,
    string MinimumVersion);

internal sealed record NativeMacOsReleaseBlocker(
    string Id,
    string Summary);

internal sealed record NativeMacOsInput(
    string Id,
    string Kind,
    string? RepositoryRelativePath,
    string? DownloadLocation,
    string? DerivedFromInputId,
    string? Derivation,
    string? BuildHostLogicalPath,
    string Sha256);

internal sealed record NativeMacOsToolchain(
    string ZigVersion,
    string ZigArchiveSha256,
    string ZigExecutableSha256,
    string ClangVersion,
    string ClangSha256,
    string SdkVersion,
    string? SdkBuild,
    string SdkSettingsSha256);

internal sealed record NativeMacOsMetallib(
    string ReleaseArchiveSha256,
    string Arm64SliceSha256,
    long Offset,
    long Length,
    string Sha256);

internal sealed record NativeMacOsBuildConfiguration(
    string GhosttyCommit,
    string GhosttyTag,
    IReadOnlyList<string> GhosttyOptions,
    IReadOnlyList<string> ShimCompilerOptions,
    NativeMacOsMetallib Metallib);

internal sealed record NativeMacOsComponent(
    string Id,
    string Name,
    string Version,
    string Inclusion,
    string InclusionBasis,
    string SelectionEvidenceInputId,
    string LicenseDeclared,
    string LicenseEvidenceStatus,
    IReadOnlyList<string> LicenseEvidenceInputIds,
    IReadOnlyList<string> BlockerIds,
    IReadOnlyList<string> DependsOnComponentIds);

internal sealed record NativeMacOsCatalog(
    string CatalogId,
    NativeMacOsTarget Target,
    string ReleaseReadiness,
    bool LegalClearance,
    string LegalConclusion,
    IReadOnlyList<NativeMacOsReleaseBlocker> ReleaseBlockers,
    IReadOnlyList<NativeMacOsInput> Inputs,
    NativeMacOsToolchain Toolchain,
    NativeMacOsBuildConfiguration Build,
    string ExpectedArtifactManifestSha256,
    string ExpectedPackagedPayloadManifestSha256,
    IReadOnlyList<NativeMacOsComponent> Components);

internal sealed record NativeMacOsCatalogDocument(
    NativeMacOsCatalog Catalog,
    byte[] RawContent,
    string Sha256);

internal sealed record NativeMacOsArtifactFile(
    string Path,
    string Role,
    long Length,
    string UnixMode,
    string Sha256);

internal sealed record NativeMacOsArtifactManifest(
    IReadOnlyList<NativeMacOsArtifactFile> Files,
    int FileCount,
    long TotalBytes,
    string ManifestSha256)
{
    public NativeMacOsArtifactManifest PackageFiles()
    {
        var files = Files
            .Where(file => file.Role == NativeMacOsArtifactRoles.Package)
            .ToArray();
        return Create(files);
    }

    public static NativeMacOsArtifactManifest Create(
        IReadOnlyList<NativeMacOsArtifactFile> files)
    {
        NativeMacOsPath.ValidatePortableUniqueness(
            files.Select(file => file.Path));
        NativeMacOsPath.ValidatePortableDirectoryUniqueness(
            files.Select(file => file.Path));
        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes = checked(totalBytes + file.Length);
        }

        return new NativeMacOsArtifactManifest(
            files,
            files.Count,
            totalBytes,
            NativeMacOsArtifactManifestDigester.Compute(files));
    }
}

internal static class NativeMacOsArtifactRoles
{
    public const string Package = "package";
    public const string BuildTestBinary = "build-test-binary";
}

internal sealed record NativeMacOsBuildReceipt(
    string Generator,
    string CatalogId,
    string CatalogSha256,
    NativeMacOsTarget Target,
    string ReleaseReadiness,
    bool LegalClearance,
    string LegalConclusion,
    NativeMacOsToolchain Toolchain,
    NativeMacOsBuildConfiguration Build,
    IReadOnlyList<NativeMacOsReceiptInput> Inputs,
    IReadOnlyList<NativeMacOsArtifactFile> ArtifactFiles,
    int ArtifactFileCount,
    long ArtifactBytes,
    string ArtifactManifestSha256,
    int PackagedFileCount,
    long PackagedBytes,
    string PackagedPayloadManifestSha256,
    IReadOnlyList<string> ComponentIds,
    IReadOnlyList<string> ReleaseBlockerIds);

internal sealed record NativeMacOsReceiptInput(
    string Id,
    string Sha256);

internal sealed record NativeMacOsBuildReceiptDocument(
    NativeMacOsBuildReceipt Receipt,
    byte[] RawContent);
