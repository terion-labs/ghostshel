using System.Security.Cryptography;

namespace GhostShell.Packaging;

internal sealed record NativeMacOsPackageProvenance(
    byte[] CatalogContent,
    byte[] ReceiptContent);

internal static class NativeMacOsProvenanceValidator
{
    private static readonly HashSet<string> AllowedRootNativeFiles =
    [
        "GhostShell",
        "createdump",
        "libAvaloniaNative.dylib",
        "libHarfBuzzSharp.dylib",
        "libSkiaSharp.dylib",
        "libSystem.Globalization.Native.dylib",
        "libSystem.IO.Compression.Native.dylib",
        "libSystem.Native.dylib",
        "libSystem.Net.Security.Native.dylib",
        "libSystem.Security.Cryptography.Native.Apple.dylib",
        "libclrgc.dylib",
        "libclrgcexp.dylib",
        "libclrjit.dylib",
        "libcoreclr.dylib",
        "libe_sqlite3.dylib",
        "libghostshell-ghostty.dylib",
        "libghostty.dylib",
        "libhostfxr.dylib",
        "libhostpolicy.dylib",
        "libmscordaccore.dylib",
        "libmscordbi.dylib",
        "libporta_pty.dylib",
    ];

    public static NativeMacOsPackageProvenance Validate(
        string executableDirectory,
        string licenseDirectory,
        string catalogPath,
        string receiptPath)
    {
        var catalogDocument =
            NativeMacOsProvenanceReader.ReadCatalog(catalogPath);
        var receiptDocument =
            NativeMacOsProvenanceReader.ReadReceipt(receiptPath);
        var nativeLicenseDirectory = Path.Combine(licenseDirectory, "Native");
        ValidateNativeLicenseDirectory(
            nativeLicenseDirectory,
            catalogDocument.Catalog);
        var copiedCatalog =
            NativeMacOsProvenanceReader.ReadCatalog(Path.Combine(
                nativeLicenseDirectory,
                NativeMacOsProvenanceSchema.CatalogFileName));
        var copiedReceipt =
            NativeMacOsProvenanceReader.ReadReceipt(Path.Combine(
                nativeLicenseDirectory,
                NativeMacOsProvenanceSchema.ReceiptFileName));
        if (!catalogDocument.RawContent.AsSpan()
                .SequenceEqual(copiedCatalog.RawContent)
            || !receiptDocument.RawContent.AsSpan()
                .SequenceEqual(copiedReceipt.RawContent))
        {
            throw new InvalidDataException(
                "The published native catalog or receipt differs from its reviewed source.");
        }
        ValidateObservedEvidenceCopy(
            nativeLicenseDirectory,
            catalogDocument.Catalog,
            "observed-ghostty-build-evidence",
            NativeMacOsProvenanceSchema.BuildEvidenceFileName);
        ValidateObservedEvidenceCopy(
            nativeLicenseDirectory,
            catalogDocument.Catalog,
            "observed-ghostty-resource-evidence",
            NativeMacOsProvenanceSchema.ResourceEvidenceFileName);

        ValidateReceiptAgainstCatalog(
            catalogDocument,
            receiptDocument.Receipt);
        ValidatePackagedPayload(
            executableDirectory,
            licenseDirectory,
            receiptDocument.Receipt);
        return new NativeMacOsPackageProvenance(
            catalogDocument.RawContent,
            receiptDocument.RawContent);
    }

    internal static void ValidateReceiptAgainstCatalog(
        NativeMacOsCatalogDocument catalogDocument,
        NativeMacOsBuildReceipt receipt)
    {
        var catalog = catalogDocument.Catalog;
        if (receipt.Generator != NativeMacOsProvenanceSchema.Generator
            || receipt.CatalogId != catalog.CatalogId
            || receipt.CatalogSha256 != catalogDocument.Sha256
            || receipt.Target != catalog.Target
            || receipt.ReleaseReadiness != catalog.ReleaseReadiness
            || receipt.LegalClearance
            || receipt.LegalClearance != catalog.LegalClearance
            || receipt.LegalConclusion != catalog.LegalConclusion
            || receipt.Toolchain != catalog.Toolchain
            || !BuildConfigurationsEqual(receipt.Build, catalog.Build)
            || receipt.ArtifactManifestSha256
                != catalog.ExpectedArtifactManifestSha256
            || receipt.PackagedPayloadManifestSha256
                != catalog.ExpectedPackagedPayloadManifestSha256)
        {
            throw new InvalidDataException(
                "The native build receipt does not match the reviewed catalog.");
        }

        var expectedInputs = catalog.Inputs
            .Select(input => new NativeMacOsReceiptInput(
                input.Id,
                input.Sha256))
            .ToArray();
        if (!receipt.Inputs.SequenceEqual(expectedInputs))
        {
            throw new InvalidDataException(
                "The native receipt input observations do not match the catalog.");
        }

        if (!receipt.ComponentIds.SequenceEqual(
                catalog.Components.Select(component => component.Id),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The native receipt component closure does not match the catalog.");
        }

        if (!receipt.ReleaseBlockerIds.SequenceEqual(
                catalog.ReleaseBlockers.Select(blocker => blocker.Id),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The native receipt release blockers do not match the catalog.");
        }

        var packagedHashes = receipt.ArtifactFiles
            .Where(file => file.Role == NativeMacOsArtifactRoles.Package)
            .Select(file => file.Sha256)
            .ToHashSet(StringComparer.Ordinal);
        var inputsById = catalog.Inputs.ToDictionary(
            input => input.Id,
            StringComparer.Ordinal);
        foreach (var component in catalog.Components.Where(component =>
                     component.LicenseEvidenceStatus == "packaged"))
        {
            if (component.LicenseEvidenceInputIds.Count == 0
                || component.LicenseEvidenceInputIds.Any(inputId =>
                    !packagedHashes.Contains(inputsById[inputId].Sha256)))
            {
                throw new InvalidDataException(
                    $"Component {component.Id} claims license evidence "
                    + "that is not present in the packaged payload.");
            }
        }
    }

    private static void ValidatePackagedPayload(
        string executableDirectory,
        string licenseDirectory,
        NativeMacOsBuildReceipt receipt)
    {
        RejectUnreceiptedNativeFiles(executableDirectory);
        var actual =
            NativeMacOsArtifactManifestInspector.InspectPackagedPayload(
                executableDirectory,
                licenseDirectory);
        var expectedFiles = receipt.ArtifactFiles
            .Where(file => file.Role == NativeMacOsArtifactRoles.Package)
            .ToArray();
        var expected = NativeMacOsArtifactManifest.Create(expectedFiles);
        if (actual.FileCount != receipt.PackagedFileCount
            || actual.TotalBytes != receipt.PackagedBytes
            || actual.ManifestSha256 != receipt.PackagedPayloadManifestSha256
            || !actual.Files.SequenceEqual(expected.Files))
        {
            throw new InvalidDataException(
                "The copied native package payload does not exactly match its receipt.");
        }
    }

    private static void ValidateNativeLicenseDirectory(
        string directoryPath,
        NativeMacOsCatalog catalog)
    {
        var directory = new DirectoryInfo(directoryPath);
        if (!directory.Exists
            || directory.LinkTarget is not null
            || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The packaged native provenance directory is missing or unsafe.");
        }

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            NativeMacOsProvenanceSchema.CatalogFileName,
            NativeMacOsProvenanceSchema.ReceiptFileName,
        };
        AddObservedEvidenceFile(
            "observed-ghostty-build-evidence",
            NativeMacOsProvenanceSchema.BuildEvidenceFileName);
        AddObservedEvidenceFile(
            "observed-ghostty-resource-evidence",
            NativeMacOsProvenanceSchema.ResourceEvidenceFileName);
        foreach (var entry in directory.EnumerateFileSystemInfos(
                     "*",
                     new EnumerationOptions
                     {
                         AttributesToSkip = 0,
                         IgnoreInaccessible = false,
                         RecurseSubdirectories = false,
                         ReturnSpecialDirectories = false,
                     }))
        {
            if (!expected.Remove(entry.Name)
                || entry is not FileInfo
                || entry.LinkTarget is not null
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "The packaged native provenance directory has unexpected entries.");
            }
        }

        if (expected.Count != 0)
        {
            throw new InvalidDataException(
                "The packaged native provenance directory is incomplete.");
        }

        void AddObservedEvidenceFile(string kind, string fileName)
        {
            var count = catalog.Inputs.Count(input => input.Kind == kind);
            if (count > 1)
            {
                throw new InvalidDataException(
                    $"The native catalog contains multiple {kind} inputs.");
            }

            if (count == 1)
            {
                expected.Add(fileName);
            }
        }
    }

    private static void ValidateObservedEvidenceCopy(
        string nativeLicenseDirectory,
        NativeMacOsCatalog catalog,
        string kind,
        string fileName)
    {
        var inputs = catalog.Inputs
            .Where(input => input.Kind == kind)
            .ToArray();
        if (inputs.Length == 0)
        {
            return;
        }

        if (inputs.Length != 1
            || Path.GetFileName(inputs[0].RepositoryRelativePath) != fileName)
        {
            throw new InvalidDataException(
                $"The native catalog has an invalid {kind} evidence binding.");
        }

        using var stream = RegularPackageFileReader.Open(
            Path.Combine(nativeLicenseDirectory, fileName),
            out var inspection);
        if (inspection.Length <= 0
            || inspection.Length > NativeMacOsProvenanceSchema.MaximumReceiptBytes)
        {
            throw new InvalidDataException(
                $"The packaged {kind} evidence has an invalid byte length.");
        }

        var observed = Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
        if (observed != inputs[0].Sha256)
        {
            throw new InvalidDataException(
                $"The packaged {kind} evidence differs from its catalog hash.");
        }
    }

    private static void RejectUnreceiptedNativeFiles(
        string executableDirectory)
    {
        var rootPath = MacOsPackagePaths.RequireExistingDirectory(
            executableDirectory,
            nameof(executableDirectory));
        var pending = new Stack<(DirectoryInfo Directory, string Prefix, int Depth)>();
        pending.Push((new DirectoryInfo(rootPath), string.Empty, 0));
        var entryCount = 0;
        var fileCount = 0;
        while (pending.Count > 0)
        {
            var (directory, prefix, depth) = pending.Pop();
            directory.Refresh();
            if (!directory.Exists
                || directory.LinkTarget is not null
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "The packaged executable tree contains an unsafe directory.");
            }

            foreach (var entry in directory.EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions
                         {
                             AttributesToSkip = 0,
                             IgnoreInaccessible = false,
                             RecurseSubdirectories = false,
                             ReturnSpecialDirectories = false,
                         }))
            {
                entryCount++;
                if (entryCount
                    > NativeMacOsProvenanceSchema.MaximumArtifactEntries)
                {
                    throw new InvalidDataException(
                        "The packaged executable tree exceeds the entry limit.");
                }

                if (entry.LinkTarget is not null
                    || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        "The packaged executable tree contains a link.");
                }

                var relativePath = string.IsNullOrEmpty(prefix)
                    ? entry.Name
                    : $"{prefix}/{entry.Name}";
                NativeMacOsPath.Validate(relativePath);
                if (entry is DirectoryInfo childDirectory)
                {
                    if (relativePath is "ghostty" or "terminfo")
                    {
                        continue;
                    }

                    if (entry.Name.EndsWith(
                            ".framework",
                            StringComparison.OrdinalIgnoreCase)
                        || entry.Name.EndsWith(
                            ".bundle",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Unreceipted native directory {relativePath} is present.");
                    }

                    if (depth + 1
                        > NativeMacOsProvenanceSchema.MaximumPathSegments)
                    {
                        throw new InvalidDataException(
                            "The packaged executable tree exceeds the depth limit.");
                    }

                    pending.Push((childDirectory, relativePath, depth + 1));
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    throw new InvalidDataException(
                        "The packaged executable tree contains a special entry.");
                }

                fileCount++;
                if (fileCount
                    > NativeMacOsProvenanceSchema.MaximumArtifactFiles)
                {
                    throw new InvalidDataException(
                        "The packaged executable tree exceeds the file limit.");
                }

                if (IsNativeFile(file)
                    && (prefix.Length != 0
                        || !AllowedRootNativeFiles.Contains(file.Name)))
                {
                    throw new InvalidDataException(
                        $"Unreceipted native file {relativePath} is present.");
                }
            }
        }
    }

    private static bool IsNativeFile(FileInfo file)
    {
        using var stream = RegularPackageFileReader.Open(
            file.FullName,
            out var inspection);
        Span<byte> magic = stackalloc byte[4];
        var magicBytes = stream.Read(magic);
        var isMachO = magicBytes == magic.Length
            && IsMachOMagic(magic);
        var extension = file.Extension.ToLowerInvariant();
        var hasNativeExtension = extension is ".a" or ".dylib" or ".so" or ".bundle";
        var isExtensionlessExecutable = extension.Length == 0
            && inspection.UnixMode is { } unixMode
            && (unixMode
                & (UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute)) != 0;
        return isMachO || hasNativeExtension || isExtensionlessExecutable;
    }

    private static bool IsMachOMagic(ReadOnlySpan<byte> magic) =>
        (magic[0], magic[1], magic[2], magic[3]) is
            (0xFE, 0xED, 0xFA, 0xCE)
            or (0xCE, 0xFA, 0xED, 0xFE)
            or (0xFE, 0xED, 0xFA, 0xCF)
            or (0xCF, 0xFA, 0xED, 0xFE)
            or (0xCA, 0xFE, 0xBA, 0xBE)
            or (0xBE, 0xBA, 0xFE, 0xCA)
            or (0xCA, 0xFE, 0xBA, 0xBF)
            or (0xBF, 0xBA, 0xFE, 0xCA);

    private static bool BuildConfigurationsEqual(
        NativeMacOsBuildConfiguration first,
        NativeMacOsBuildConfiguration second) =>
        first.GhosttyCommit == second.GhosttyCommit
        && first.GhosttyTag == second.GhosttyTag
        && first.GhosttyOptions.SequenceEqual(
            second.GhosttyOptions,
            StringComparer.Ordinal)
        && first.ShimCompilerOptions.SequenceEqual(
            second.ShimCompilerOptions,
            StringComparer.Ordinal)
        && first.Metallib == second.Metallib;
}
