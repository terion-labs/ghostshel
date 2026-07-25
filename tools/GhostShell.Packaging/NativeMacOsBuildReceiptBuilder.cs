using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GhostShell.Packaging;

internal sealed record NativeMacOsBuildReceiptRequest(
    string CatalogPath,
    string ArtifactDirectory,
    string OutputPath,
    string RepositoryRoot,
    string GhosttySourceDirectory,
    string ZigArchivePath,
    string ZigExecutablePath,
    string ZigLibraryDirectory,
    string ZigLocalCacheDirectory,
    string ZigGlobalCacheDirectory,
    string ZigBuildTracePath,
    string GhosttyInstallDirectory,
    string ClangExecutablePath,
    string SdkDirectory,
    string SdkSettingsPath,
    string ReleaseArchivePath,
    string ReleaseArm64BinaryPath,
    string MetallibPath,
    string ArtifactLibGhosttyPath,
    string ZigVersion,
    string GhosttyCommit,
    string GhosttyTag,
    string ClangVersion,
    string SdkVersion,
    string? SdkBuild,
    IReadOnlyList<string> GhosttyOptions,
    IReadOnlyList<string> ShimCompilerOptions);

internal sealed record NativeMacOsBuildReceiptResult(
    string OutputPath,
    int ArtifactFileCount,
    long ArtifactBytes,
    string ArtifactManifestSha256);

internal static class NativeMacOsBuildReceiptBuilder
{
    public static NativeMacOsBuildReceiptResult Create(
        NativeMacOsBuildReceiptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalogDocument =
            NativeMacOsProvenanceReader.ReadCatalog(request.CatalogPath);
        ValidateObservedConfiguration(catalogDocument.Catalog, request);
        var observedInputs = ObserveInputs(
            catalogDocument.Catalog,
            request);
        ValidateMetallibExtraction(catalogDocument.Catalog, request);

        var artifactRoot = MacOsPackagePaths.RequireExistingDirectory(
            request.ArtifactDirectory,
            nameof(request.ArtifactDirectory));
        var outputPath = RequireReceiptOutputPath(
            artifactRoot,
            request.OutputPath);
        var artifactManifest =
            NativeMacOsArtifactManifestInspector.InspectBuildArtifacts(
                artifactRoot);
        var packagedManifest = artifactManifest.PackageFiles();
        RequireExpectedDigest(
            artifactManifest.ManifestSha256,
            catalogDocument.Catalog.ExpectedArtifactManifestSha256,
            "artifact");
        RequireExpectedDigest(
            packagedManifest.ManifestSha256,
            catalogDocument.Catalog.ExpectedPackagedPayloadManifestSha256,
            "packaged payload");

        var catalog = catalogDocument.Catalog;
        var receipt = new NativeMacOsBuildReceipt(
            NativeMacOsProvenanceSchema.Generator,
            catalog.CatalogId,
            catalogDocument.Sha256,
            catalog.Target,
            catalog.ReleaseReadiness,
            catalog.LegalClearance,
            catalog.LegalConclusion,
            catalog.Toolchain,
            catalog.Build,
            observedInputs,
            artifactManifest.Files,
            artifactManifest.FileCount,
            artifactManifest.TotalBytes,
            artifactManifest.ManifestSha256,
            packagedManifest.FileCount,
            packagedManifest.TotalBytes,
            packagedManifest.ManifestSha256,
            catalog.Components.Select(component => component.Id).ToArray(),
            catalog.ReleaseBlockers.Select(blocker => blocker.Id).ToArray());
        NativeMacOsProvenanceValidator.ValidateReceiptAgainstCatalog(
            catalogDocument,
            receipt);
        var receiptBytes = NativeMacOsBuildReceiptWriter.Write(receipt);
        _ = NativeMacOsProvenanceReader.ParseReceipt(receiptBytes);
        WriteAtomically(outputPath, receiptBytes);
        return new NativeMacOsBuildReceiptResult(
            outputPath,
            artifactManifest.FileCount,
            artifactManifest.TotalBytes,
            artifactManifest.ManifestSha256);
    }

    private static void ValidateObservedConfiguration(
        NativeMacOsCatalog catalog,
        NativeMacOsBuildReceiptRequest request)
    {
        RequireExact(
            request.ZigVersion,
            catalog.Toolchain.ZigVersion,
            "Zig version");
        RequireExact(
            request.GhosttyCommit,
            catalog.Build.GhosttyCommit,
            "Ghostty commit");
        RequireExact(
            request.GhosttyTag,
            catalog.Build.GhosttyTag,
            "Ghostty tag");
        RequireExact(
            request.ClangVersion,
            catalog.Toolchain.ClangVersion,
            "clang version");
        RequireExact(
            request.SdkVersion,
            catalog.Toolchain.SdkVersion,
            "SDK version");
        RequireExact(
            request.SdkBuild,
            catalog.Toolchain.SdkBuild,
            "SDK build");
        RequireSequence(
            request.GhosttyOptions,
            catalog.Build.GhosttyOptions,
            "Ghostty options");
        RequireSequence(
            request.ShimCompilerOptions,
            catalog.Build.ShimCompilerOptions,
            "shim compiler options");
    }

    private static IReadOnlyList<NativeMacOsReceiptInput> ObserveInputs(
        NativeMacOsCatalog catalog,
        NativeMacOsBuildReceiptRequest request)
    {
        var repositoryRoot = MacOsPackagePaths.RequireExistingDirectory(
            request.RepositoryRoot,
            nameof(request.RepositoryRoot));
        var ghosttySource = MacOsPackagePaths.RequireExistingDirectory(
            request.GhosttySourceDirectory,
            nameof(request.GhosttySourceDirectory));
        var concretePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zig-archive"] = request.ZigArchivePath,
            ["zig-executable"] = request.ZigExecutablePath,
            ["clang-executable"] = request.ClangExecutablePath,
            ["sdk-settings"] = request.SdkSettingsPath,
            ["ghostty-release-archive"] = request.ReleaseArchivePath,
            ["ghostty-release-arm64-binary"] = request.ReleaseArm64BinaryPath,
            ["ghostty-metallib"] = request.MetallibPath,
        };
        var observedBuildEvidence = ObserveBuildEvidenceIfRequired(
            catalog,
            request);
        var observedResourceEvidence = ObserveResourceEvidenceIfRequired(
            catalog,
            request);
        var observations = new List<NativeMacOsReceiptInput>(
            catalog.Inputs.Count);
        long cumulativeBytes = 0;
        foreach (var input in catalog.Inputs)
        {
            var path = input.Kind switch
            {
                "repository-file" => ResolveRelativeFile(
                    repositoryRoot,
                    input.RepositoryRelativePath!),
                "observed-ghostty-build-evidence" => ResolveRelativeFile(
                    repositoryRoot,
                    input.RepositoryRelativePath!),
                "observed-ghostty-resource-evidence" => ResolveRelativeFile(
                    repositoryRoot,
                    input.RepositoryRelativePath!),
                "ghostty-source-file"
                    or "ghostty-build-evidence-file" => ResolveRelativeFile(
                    ghosttySource,
                    input.RepositoryRelativePath!),
                _ => concretePaths[input.Kind],
            };
            var observedHash = HashRegularFile(path, ref cumulativeBytes);
            if (observedHash != input.Sha256)
            {
                throw new InvalidDataException(
                    $"Native input {input.Id} does not match its reviewed SHA-256.");
            }
            if (input.Kind == "observed-ghostty-build-evidence"
                && observedBuildEvidence!.Sha256 != observedHash)
            {
                throw new InvalidDataException(
                    "The isolated Ghostty build does not match the reviewed "
                    + "normalized build evidence.");
            }
            if (input.Kind == "observed-ghostty-resource-evidence"
                && observedResourceEvidence!.Sha256 != observedHash)
            {
                throw new InvalidDataException(
                    "The installed Ghostty resources do not match the reviewed "
                    + "normalized resource evidence.");
            }

            observations.Add(new NativeMacOsReceiptInput(
                input.Id,
                observedHash));
        }

        return observations;
    }

    private static NativeMacOsBuildEvidenceResult? ObserveBuildEvidenceIfRequired(
        NativeMacOsCatalog catalog,
        NativeMacOsBuildReceiptRequest request)
    {
        var evidenceInputs = catalog.Inputs
            .Where(input => input.Kind == "observed-ghostty-build-evidence")
            .ToArray();
        if (evidenceInputs.Length == 0)
        {
            return null;
        }

        if (evidenceInputs.Length != 1)
        {
            throw new InvalidDataException(
                "The native catalog must contain at most one observed Ghostty "
                + "build evidence input.");
        }

        return NativeMacOsBuildEvidenceBuilder.Observe(
            new NativeMacOsBuildEvidenceRequest(
                request.ZigBuildTracePath,
                request.RepositoryRoot,
                request.GhosttySourceDirectory,
                request.ZigExecutablePath,
                request.ZigLibraryDirectory,
                request.ZigLocalCacheDirectory,
                request.ZigGlobalCacheDirectory,
                request.SdkDirectory,
                request.MetallibPath,
                request.GhosttyInstallDirectory,
                request.ArtifactLibGhosttyPath));
    }

    private static NativeMacOsResourceEvidenceResult?
        ObserveResourceEvidenceIfRequired(
            NativeMacOsCatalog catalog,
            NativeMacOsBuildReceiptRequest request)
    {
        var evidenceInputs = catalog.Inputs
            .Where(input => input.Kind == "observed-ghostty-resource-evidence")
            .ToArray();
        if (evidenceInputs.Length == 0)
        {
            return null;
        }

        if (evidenceInputs.Length != 1)
        {
            throw new InvalidDataException(
                "The native catalog must contain at most one observed Ghostty "
                + "resource evidence input.");
        }

        return NativeMacOsResourceEvidenceBuilder.Observe(
            new NativeMacOsResourceEvidenceRequest(
                request.GhosttySourceDirectory,
                request.ZigGlobalCacheDirectory,
                request.GhosttyInstallDirectory));
    }

    private static string ResolveRelativeFile(string root, string relativePath)
    {
        NativeMacOsPath.Validate(relativePath);
        var current = root;
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists
                || directory.LinkTarget is not null
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"Native input path {relativePath} has an unsafe directory ancestor.");
            }
        }

        return Path.Combine(current, segments[^1]);
    }

    private static string HashRegularFile(string path, ref long cumulativeBytes)
    {
        using var stream = RegularPackageFileReader.Open(path, out var inspection);
        if (inspection.Length < 0
            || inspection.Length > NativeMacOsProvenanceSchema.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                "A native build input exceeds the byte limit.");
        }

        try
        {
            cumulativeBytes = checked(cumulativeBytes + inspection.Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "The native build input byte count overflowed.",
                exception);
        }

        if (cumulativeBytes > NativeMacOsProvenanceSchema.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                "The native build inputs exceed the cumulative byte limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(131_072);
        try
        {
            var remaining = inspection.Length;
            while (remaining > 0)
            {
                var read = stream.Read(
                    buffer,
                    0,
                    (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidDataException(
                        "A native build input became shorter while hashing.");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "A native build input became longer while hashing.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateMetallibExtraction(
        NativeMacOsCatalog catalog,
        NativeMacOsBuildReceiptRequest request)
    {
        var expected = catalog.Build.Metallib;
        using var metallib = RegularPackageFileReader.Open(
            request.MetallibPath,
            out var metallibInspection);
        if (metallibInspection.Length != expected.Length)
        {
            throw new InvalidDataException(
                "The extracted Ghostty metallib length does not match the catalog.");
        }

        using var arm64Binary = RegularPackageFileReader.Open(
            request.ReleaseArm64BinaryPath,
            out var arm64Inspection);
        long rangeEnd;
        try
        {
            rangeEnd = checked(expected.Offset + expected.Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "The reviewed metallib byte range overflowed.",
                exception);
        }

        if (rangeEnd > arm64Inspection.Length)
        {
            throw new InvalidDataException(
                "The reviewed metallib byte range exceeds the arm64 release binary.");
        }

        arm64Binary.Seek(expected.Offset, SeekOrigin.Begin);
        using var releaseHash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var metallibHash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var releaseBuffer = ArrayPool<byte>.Shared.Rent(131_072);
        var metallibBuffer = ArrayPool<byte>.Shared.Rent(131_072);
        try
        {
            var remaining = expected.Length;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(releaseBuffer.Length, remaining);
                ReadExactly(arm64Binary, releaseBuffer, requested);
                ReadExactly(metallib, metallibBuffer, requested);

                if (!releaseBuffer.AsSpan(0, requested)
                        .SequenceEqual(metallibBuffer.AsSpan(0, requested)))
                {
                    throw new InvalidDataException(
                        "The extracted metallib bytes differ from the release binary range.");
                }

                releaseHash.AppendData(releaseBuffer, 0, requested);
                metallibHash.AppendData(metallibBuffer, 0, requested);
                remaining -= requested;
            }

            if (metallib.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "The extracted metallib became longer during validation.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(releaseBuffer);
            ArrayPool<byte>.Shared.Return(metallibBuffer);
        }

        var extractedHash = Convert.ToHexString(releaseHash.GetHashAndReset())
            .ToLowerInvariant();
        var metallibFileHash =
            Convert.ToHexString(metallibHash.GetHashAndReset())
                .ToLowerInvariant();
        if (extractedHash != expected.Sha256
            || metallibFileHash != expected.Sha256)
        {
            throw new InvalidDataException(
                "The reviewed metallib range does not match the extracted metallib.");
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "A metallib input ended inside the reviewed byte range.");
            }

            offset += read;
        }
    }

    private static string RequireReceiptOutputPath(
        string artifactRoot,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "The native receipt output must have a parent directory.",
                nameof(outputPath));
        var physicalParent = MacOsPackagePaths.RequireExistingDirectory(
            parent,
            nameof(outputPath));
        if (!MacOsPackagePaths.AreSameDirectory(artifactRoot, physicalParent)
            || Path.GetFileName(fullPath)
                != NativeMacOsProvenanceSchema.ReceiptFileName)
        {
            throw new ArgumentException(
                "The native receipt must be written at the artifact root.",
                nameof(outputPath));
        }

        return Path.Combine(
            artifactRoot,
            NativeMacOsProvenanceSchema.ReceiptFileName);
    }

    private static void WriteAtomically(string outputPath, byte[] content)
    {
        var parent = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidDataException(
                "The native receipt output has no parent directory.");
        var temporaryPath = Path.Combine(
            parent,
            $".{NativeMacOsProvenanceSchema.ReceiptFileName}."
            + $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 131_072,
                       FileOptions.SequentialScan))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RequireExpectedDigest(
        string actual,
        string expected,
        string label)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"The native {label} manifest digest is {actual}; "
                + $"the reviewed catalog requires {expected}.");
        }
    }

    private static void RequireExact(
        string? actual,
        string? expected,
        string label)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The observed {label} does not match the reviewed catalog.");
        }
    }

    private static void RequireSequence(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected,
        string label)
    {
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The observed {label} do not match the reviewed catalog.");
        }
    }
}

internal static class NativeMacOsBuildReceiptWriter
{
    public static byte[] Write(NativeMacOsBuildReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var stream = new NativeMacOsBoundedWriteStream(
            NativeMacOsProvenanceSchema.MaximumReceiptBytes);
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", NativeMacOsProvenanceSchema.Version);
            writer.WriteString("generator", receipt.Generator);
            writer.WriteStartObject("catalog");
            writer.WriteString("id", receipt.CatalogId);
            writer.WriteString("sha256", receipt.CatalogSha256);
            writer.WriteEndObject();
            WriteTarget(writer, receipt.Target);
            writer.WriteString("releaseReadiness", receipt.ReleaseReadiness);
            writer.WriteBoolean("legalClearance", receipt.LegalClearance);
            writer.WriteString("legalConclusion", receipt.LegalConclusion);
            WriteToolchain(writer, receipt.Toolchain);
            WriteBuild(writer, receipt.Build);
            writer.WriteStartArray("inputs");
            foreach (var input in receipt.Inputs)
            {
                writer.WriteStartObject();
                writer.WriteString("id", input.Id);
                writer.WriteString("sha256", input.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("artifactFiles");
            foreach (var file in receipt.ArtifactFiles)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("role", file.Role);
                writer.WriteNumber("length", file.Length);
                writer.WriteString("unixMode", file.UnixMode);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("artifactFileCount", receipt.ArtifactFileCount);
            writer.WriteNumber("artifactBytes", receipt.ArtifactBytes);
            writer.WriteString(
                "artifactManifestSha256",
                receipt.ArtifactManifestSha256);
            writer.WriteNumber("packagedFileCount", receipt.PackagedFileCount);
            writer.WriteNumber("packagedBytes", receipt.PackagedBytes);
            writer.WriteString(
                "packagedPayloadManifestSha256",
                receipt.PackagedPayloadManifestSha256);
            WriteStringArray(writer, "componentIds", receipt.ComponentIds);
            WriteStringArray(
                writer,
                "releaseBlockerIds",
                receipt.ReleaseBlockerIds);
            writer.WriteEndObject();
            writer.Flush();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteTarget(
        Utf8JsonWriter writer,
        NativeMacOsTarget target)
    {
        writer.WriteStartObject("target");
        writer.WriteString("os", target.OperatingSystem);
        writer.WriteString("architecture", target.Architecture);
        writer.WriteString("minimumVersion", target.MinimumVersion);
        writer.WriteEndObject();
    }

    private static void WriteToolchain(
        Utf8JsonWriter writer,
        NativeMacOsToolchain toolchain)
    {
        writer.WriteStartObject("toolchain");
        writer.WriteString("zigVersion", toolchain.ZigVersion);
        writer.WriteString("zigArchiveSha256", toolchain.ZigArchiveSha256);
        writer.WriteString("zigExecutableSha256", toolchain.ZigExecutableSha256);
        writer.WriteString("clangVersion", toolchain.ClangVersion);
        writer.WriteString("clangSha256", toolchain.ClangSha256);
        writer.WriteString("sdkVersion", toolchain.SdkVersion);
        if (toolchain.SdkBuild is not null)
        {
            writer.WriteString("sdkBuild", toolchain.SdkBuild);
        }

        writer.WriteString(
            "sdkSettingsSha256",
            toolchain.SdkSettingsSha256);
        writer.WriteEndObject();
    }

    private static void WriteBuild(
        Utf8JsonWriter writer,
        NativeMacOsBuildConfiguration build)
    {
        writer.WriteStartObject("build");
        writer.WriteString("ghosttyCommit", build.GhosttyCommit);
        writer.WriteString("ghosttyTag", build.GhosttyTag);
        WriteStringArray(writer, "ghosttyOptions", build.GhosttyOptions);
        WriteStringArray(
            writer,
            "shimCompilerOptions",
            build.ShimCompilerOptions);
        writer.WriteStartObject("metallib");
        writer.WriteString(
            "releaseArchiveSha256",
            build.Metallib.ReleaseArchiveSha256);
        writer.WriteString(
            "arm64SliceSha256",
            build.Metallib.Arm64SliceSha256);
        writer.WriteNumber("offset", build.Metallib.Offset);
        writer.WriteNumber("length", build.Metallib.Length);
        writer.WriteString("sha256", build.Metallib.Sha256);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private sealed class NativeMacOsBoundedWriteStream(long maximumBytes)
        : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureRemaining(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureRemaining(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureRemaining(1);
            base.WriteByte(value);
        }

        private void EnsureRemaining(int count)
        {
            if (count < 0 || Position > maximumBytes - count)
            {
                throw new InvalidDataException(
                    "The generated native build receipt exceeds the byte limit.");
            }
        }
    }
}
