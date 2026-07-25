using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GhostShell.Packaging;

namespace GhostShell.AccessibilityAcceptance;

public sealed class NativeMacOsProvenanceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-native-provenance-tests-{Guid.NewGuid():N}");

    public NativeMacOsProvenanceTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Fixture_emits_deterministic_strict_receipt_bound_to_package_payload()
    {
        var first = CreateFixture("first");
        var second = CreateFixture("second");

        Assert.Equal(
            File.ReadAllBytes(first.ReceiptPath),
            File.ReadAllBytes(second.ReceiptPath));
        var receipt =
            NativeMacOsProvenanceReader.ReadReceipt(first.ReceiptPath).Receipt;
        Assert.False(receipt.LegalClearance);
        Assert.Equal("BLOCKED", receipt.ReleaseReadiness);
        Assert.Equal("NOT_ASSERTED", receipt.LegalConclusion);
        Assert.Equal(6, receipt.ArtifactFileCount);
        Assert.Equal(5, receipt.PackagedFileCount);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("legal-overclaim")]
    [InlineData("wrong-generator")]
    public void Strict_catalog_rejects_schema_and_clearance_tampering(string mutation)
    {
        var fixture = CreateFixture("catalog-tamper");
        var text = File.ReadAllText(fixture.CatalogPath);
        text = mutation switch
        {
            "unknown" => text.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1, \"unexpected\": true,",
                StringComparison.Ordinal),
            "duplicate" => text.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1, \"schemaVersion\": 1,",
                StringComparison.Ordinal),
            "legal-overclaim" => text.Replace(
                "\"legalClearance\": false",
                "\"legalClearance\": true",
                StringComparison.Ordinal),
            "wrong-generator" => text.Replace(
                NativeMacOsProvenanceSchema.Generator,
                "other-generator",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };
        File.WriteAllText(fixture.CatalogPath, text);

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsProvenanceReader.ReadCatalog(fixture.CatalogPath));
    }

    [Theory]
    [InlineData("artifactFileCount", "999")]
    [InlineData("artifactBytes", "999")]
    [InlineData(
        "artifactManifestSha256",
        "\"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"")]
    [InlineData("legalClearance", "true")]
    public void Strict_receipt_rejects_tampered_summaries_and_clearance(
        string property,
        string replacement)
    {
        var fixture = CreateFixture("receipt-tamper");
        var root = JsonNode.Parse(
            File.ReadAllText(fixture.ReceiptPath))!.AsObject();
        root[property] = JsonNode.Parse(replacement);
        File.WriteAllText(fixture.ReceiptPath, root.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsProvenanceReader.ReadReceipt(fixture.ReceiptPath));
    }

    [Fact]
    public void Artifact_inspector_rejects_traversal_shape_links_and_special_files()
    {
        var fixture = CreateFixture("links");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(fixture.ArtifactDirectory, "ghostty", "linked");
        File.CreateSymbolicLink(
            link,
            Path.Combine(fixture.ArtifactDirectory, "GHOSTTY-LICENSE"));

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsArtifactManifestInspector.InspectBuildArtifacts(
                fixture.ArtifactDirectory));
    }

    [Fact]
    public void Package_validation_rejects_missing_changed_and_extra_resources()
    {
        var fixture = CreateFixture("payload-tamper");
        var bundleInputs = fixture.CreateBundleInputs();
        File.AppendAllText(
            Path.Combine(bundleInputs.ExecutableDirectory, "ghostty", "themes", "default"),
            "changed");
        File.WriteAllText(
            Path.Combine(bundleInputs.ExecutableDirectory, "ghostty", "themes", "extra"),
            "extra");

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsProvenanceValidator.Validate(
                bundleInputs.ExecutableDirectory,
                bundleInputs.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Theory]
    [InlineData("plugins/evil.DYLIB", false)]
    [InlineData("Foo.framework/Foo", false)]
    [InlineData("plugins/payload.bin", true)]
    public void Package_validation_rejects_nested_unreceipted_native_files(
        string relativePath,
        bool hasMachOMagic)
    {
        var fixture = CreateFixture("nested-native");
        var bundleInputs = fixture.CreateBundleInputs();
        var path = Path.Combine(
            bundleInputs.ExecutableDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            hasMachOMagic
                ? [0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0]
                : "not a Mach-O file"u8.ToArray());

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsProvenanceValidator.Validate(
                bundleInputs.ExecutableDirectory,
                bundleInputs.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Fact]
    public void Package_validation_requires_byte_exact_native_evidence_copies()
    {
        var fixture = CreateFixture("evidence-copy");
        var bundleInputs = fixture.CreateBundleInputs();
        File.AppendAllText(
            Path.Combine(
                bundleInputs.LicenseDirectory,
                "Native",
                NativeMacOsProvenanceSchema.ReceiptFileName),
            " ");

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsProvenanceValidator.Validate(
                bundleInputs.ExecutableDirectory,
                bundleInputs.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Fact]
    public void Atomic_receipt_generation_preserves_existing_output_on_input_failure()
    {
        var fixture = CreateFixture("atomic");
        var original = File.ReadAllBytes(fixture.ReceiptPath);
        var request = fixture.CreateReceiptRequest();
        File.AppendAllText(request.RepositoryRoot + "/shim-source", "tampered");

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildReceiptBuilder.Create(request));
        Assert.Equal(original, File.ReadAllBytes(fixture.ReceiptPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private NativeTestFixture CreateFixture(string name)
    {
        var root = Path.Combine(_temporaryDirectory, name);
        return NativeMacOsTestProvenance.CreateStandalone(root);
    }
}

internal sealed record NativeMacOsTestEvidence(
    string CatalogPath,
    string ReceiptPath);

internal static class NativeMacOsTestProvenance
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static NativeMacOsTestEvidence AddToPublish(
        string publishDirectory,
        string fixtureDirectory)
    {
        var fixture = Create(
            publishDirectory,
            Path.Combine(fixtureDirectory, "native"));
        File.Copy(
            fixture.CatalogPath,
            Path.Combine(
                publishDirectory,
                NativeMacOsProvenanceSchema.CatalogFileName));
        File.Copy(
            fixture.ReceiptPath,
            Path.Combine(
                publishDirectory,
                NativeMacOsProvenanceSchema.ReceiptFileName));
        return new NativeMacOsTestEvidence(
            fixture.CatalogPath,
            fixture.ReceiptPath);
    }

    public static NativeTestFixture CreateStandalone(string root)
    {
        var publish = Path.Combine(root, "publish");
        Directory.CreateDirectory(publish);
        WriteFile(publish, "libghostshell-ghostty.dylib", "shim");
        WriteFile(publish, "libghostty.dylib", "ghostty");
        WriteFile(publish, "GHOSTTY-LICENSE", "ghostty license");
        WriteFile(publish, "ghostty/themes/default", "theme");
        WriteFile(publish, "terminfo/78/xterm-ghostty", "terminfo");
        return Create(publish, Path.Combine(root, "evidence"));
    }

    private static NativeTestFixture Create(
        string publishDirectory,
        string evidenceDirectory)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var artifactDirectory = Path.Combine(evidenceDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        CopyFile(publishDirectory, artifactDirectory, "libghostshell-ghostty.dylib");
        CopyFile(publishDirectory, artifactDirectory, "libghostty.dylib");
        CopyFile(publishDirectory, artifactDirectory, "GHOSTTY-LICENSE");
        CopyTree(publishDirectory, artifactDirectory, "ghostty");
        CopyTree(publishDirectory, artifactDirectory, "terminfo");
        WriteFile(artifactDirectory, "ghostshell-ghostty-smoke", "smoke");
        var manifest =
            NativeMacOsArtifactManifestInspector.InspectBuildArtifacts(
                artifactDirectory);
        var packaged = manifest.PackageFiles();

        var repositoryRoot = Path.Combine(evidenceDirectory, "repository");
        var ghosttyRoot = Path.Combine(evidenceDirectory, "ghostty-source");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(ghosttyRoot);
        WriteFile(repositoryRoot, "shim-source", "shim source");
        WriteFile(ghosttyRoot, "selection.txt", "selection");
        File.Copy(
            Path.Combine(publishDirectory, "GHOSTTY-LICENSE"),
            Path.Combine(ghosttyRoot, "LICENSE"));
        var external = CreateExternalInputs(evidenceDirectory);
        var catalogPath = Path.Combine(
            evidenceDirectory,
            NativeMacOsProvenanceSchema.CatalogFileName);
        File.WriteAllBytes(
            catalogPath,
            CreateCatalog(
                manifest,
                packaged,
                repositoryRoot,
                ghosttyRoot,
                external));
        var catalog = NativeMacOsProvenanceReader.ReadCatalog(catalogPath);
        var receiptPath = Path.Combine(
            artifactDirectory,
            NativeMacOsProvenanceSchema.ReceiptFileName);
        var receipt = new NativeMacOsBuildReceipt(
            NativeMacOsProvenanceSchema.Generator,
            catalog.Catalog.CatalogId,
            catalog.Sha256,
            catalog.Catalog.Target,
            catalog.Catalog.ReleaseReadiness,
            LegalClearance: false,
            catalog.Catalog.LegalConclusion,
            catalog.Catalog.Toolchain,
            catalog.Catalog.Build,
            catalog.Catalog.Inputs.Select(input =>
                new NativeMacOsReceiptInput(input.Id, input.Sha256)).ToArray(),
            manifest.Files,
            manifest.FileCount,
            manifest.TotalBytes,
            manifest.ManifestSha256,
            packaged.FileCount,
            packaged.TotalBytes,
            packaged.ManifestSha256,
            catalog.Catalog.Components.Select(component => component.Id).ToArray(),
            catalog.Catalog.ReleaseBlockers.Select(blocker => blocker.Id).ToArray());
        File.WriteAllBytes(
            receiptPath,
            NativeMacOsBuildReceiptWriter.Write(receipt));
        return new NativeTestFixture(
            publishDirectory,
            evidenceDirectory,
            artifactDirectory,
            catalogPath,
            receiptPath,
            repositoryRoot,
            ghosttyRoot,
            external);
    }

    private static byte[] CreateCatalog(
        NativeMacOsArtifactManifest manifest,
        NativeMacOsArtifactManifest packaged,
        string repositoryRoot,
        string ghosttyRoot,
        NativeExternalInputs external)
    {
        var licenseHash = HashFile(Path.Combine(ghosttyRoot, "LICENSE"));
        var shimHash = HashFile(Path.Combine(repositoryRoot, "shim-source"));
        var selectionHash = HashFile(Path.Combine(ghosttyRoot, "selection.txt"));
        var inputs = new object[]
        {
            new
            {
                id = "clang",
                kind = "clang-executable",
                buildHostLogicalPath = "xcrun:clang",
                sha256 = HashFile(external.Clang),
            },
            new
            {
                id = "ghostty-license",
                kind = "ghostty-source-file",
                repositoryRelativePath = "LICENSE",
                sha256 = licenseHash,
            },
            new
            {
                id = "metallib",
                kind = "ghostty-metallib",
                derivedFromInputId = "release-arm64",
                derivation = "byte-range:0:1",
                sha256 = HashFile(external.Metallib),
            },
            new
            {
                id = "release-archive",
                kind = "ghostty-release-archive",
                downloadLocation = "https://example.test/ghostty.zip",
                sha256 = HashFile(external.ReleaseArchive),
            },
            new
            {
                id = "release-arm64",
                kind = "ghostty-release-arm64-binary",
                derivedFromInputId = "release-archive",
                derivation = "mach-o-universal-slice:arm64",
                sha256 = HashFile(external.ReleaseArm64),
            },
            new
            {
                id = "sdk",
                kind = "sdk-settings",
                buildHostLogicalPath = "sdk:MacOSX15.4.sdk/SDKSettings.json",
                sha256 = HashFile(external.SdkSettings),
            },
            new
            {
                id = "selection",
                kind = "ghostty-build-evidence-file",
                repositoryRelativePath = "selection.txt",
                sha256 = selectionHash,
            },
            new
            {
                id = "shim-source",
                kind = "repository-file",
                repositoryRelativePath = "shim-source",
                sha256 = shimHash,
            },
            new
            {
                id = "zig-archive",
                kind = "zig-archive",
                downloadLocation = "https://example.test/zig.tar.xz",
                sha256 = HashFile(external.ZigArchive),
            },
            new
            {
                id = "zig-executable",
                kind = "zig-executable",
                derivedFromInputId = "zig-archive",
                derivation = "tar-xz-entry:zig/zig",
                sha256 = HashFile(external.ZigExecutable),
            },
        };
        var toolchain = new
        {
            zigVersion = "1.0.0",
            zigArchiveSha256 = HashFile(external.ZigArchive),
            zigExecutableSha256 = HashFile(external.ZigExecutable),
            clangVersion = "Test clang 1.0",
            clangSha256 = HashFile(external.Clang),
            sdkVersion = "MacOSX15.4.sdk",
            sdkSettingsSha256 = HashFile(external.SdkSettings),
        };
        var build = new
        {
            ghosttyCommit = new string('a', 40),
            ghosttyTag = "v1.0.0",
            ghosttyOptions = new[] { "-Dtest=true" },
            shimCompilerOptions = new[] { "-Wall" },
            metallib = new
            {
                releaseArchiveSha256 = HashFile(external.ReleaseArchive),
                arm64SliceSha256 = HashFile(external.ReleaseArm64),
                offset = 0,
                length = 1,
                sha256 = HashFile(external.Metallib),
            },
        };
        var catalog = new
        {
            schemaVersion = 1,
            catalogId = "ghostshell-native-test",
            receiptGenerator = NativeMacOsProvenanceSchema.Generator,
            target = new
            {
                os = "macos",
                architecture = "arm64",
                minimumVersion = "13.0",
            },
            releaseReadiness = "BLOCKED",
            legalClearance = false,
            legalConclusion = "NOT_ASSERTED",
            releaseBlockers = new[]
            {
                new
                {
                    id = "license-set-not-packaged",
                    summary = "Test release remains blocked on complete license evidence.",
                },
            },
            inputs,
            toolchain,
            build,
            expectedArtifactManifestSha256 = manifest.ManifestSha256,
            expectedPackagedPayloadManifestSha256 = packaged.ManifestSha256,
            components = new object[]
            {
                new
                {
                    id = "ghostshell-ghostty-shim",
                    name = "Test shim",
                    version = "1.0.0",
                    inclusion = "packaged-native",
                    inclusionBasis = "Test repository source.",
                    selectionEvidenceInputId = "shim-source",
                    licenseDeclared = "NOASSERTION",
                    licenseEvidenceStatus = "missing",
                    licenseEvidenceInputIds = Array.Empty<string>(),
                    blockerIds = new[] { "license-set-not-packaged" },
                    dependsOnComponentIds = new[] { "ghostty" },
                },
                new
                {
                    id = "ghostty",
                    name = "Test Ghostty",
                    version = "1.0.0",
                    inclusion = "packaged-native",
                    inclusionBasis = "Test selection manifest.",
                    selectionEvidenceInputId = "selection",
                    licenseDeclared = "MIT",
                    licenseEvidenceStatus = "packaged",
                    licenseEvidenceInputIds = new[] { "ghostty-license" },
                    blockerIds = new[] { "license-set-not-packaged" },
                    dependsOnComponentIds = Array.Empty<string>(),
                },
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
    }

    private static NativeExternalInputs CreateExternalInputs(string root)
    {
        var directory = Path.Combine(root, "external");
        Directory.CreateDirectory(directory);
        var clang = WriteFile(directory, "clang", "clang");
        var sdk = WriteFile(directory, "sdk-settings", "sdk");
        var releaseArchive = WriteFile(directory, "release.zip", "release");
        var releaseArm64 = WriteFile(directory, "release-arm64", "m");
        var metallib = WriteFile(directory, "metallib", "m");
        var zigArchive = WriteFile(directory, "zig.tar.xz", "zig archive");
        var zigExecutable = WriteFile(directory, "zig", "zig executable");
        return new NativeExternalInputs(
            clang,
            sdk,
            releaseArchive,
            releaseArm64,
            metallib,
            zigArchive,
            zigExecutable);
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static void CopyFile(string sourceRoot, string targetRoot, string path)
    {
        var source = Path.Combine(sourceRoot, path);
        var target = Path.Combine(targetRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }

    private static void CopyTree(string sourceRoot, string targetRoot, string path)
    {
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(sourceRoot, path),
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            CopyFile(sourceRoot, targetRoot, relative);
        }
    }

    private static string WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}

internal sealed record NativeExternalInputs(
    string Clang,
    string SdkSettings,
    string ReleaseArchive,
    string ReleaseArm64,
    string Metallib,
    string ZigArchive,
    string ZigExecutable);

internal sealed record NativeBundleInputs(
    string ExecutableDirectory,
    string LicenseDirectory);

internal sealed record NativeTestFixture(
    string PublishDirectory,
    string EvidenceDirectory,
    string ArtifactDirectory,
    string CatalogPath,
    string ReceiptPath,
    string RepositoryRoot,
    string GhosttyRoot,
    NativeExternalInputs External)
{
    public NativeBundleInputs CreateBundleInputs()
    {
        var root = Path.Combine(
            EvidenceDirectory,
            $"bundle-{Guid.NewGuid():N}");
        var executable = Path.Combine(root, "MacOS");
        var licenses = Path.Combine(root, "Licenses");
        Directory.CreateDirectory(executable);
        Directory.CreateDirectory(licenses);
        NativeMacOsTestProvenanceCopy.CopyPayload(
            PublishDirectory,
            executable,
            licenses,
            CatalogPath,
            ReceiptPath);
        return new NativeBundleInputs(executable, licenses);
    }

    public NativeMacOsBuildReceiptRequest CreateReceiptRequest()
    {
        var catalog = NativeMacOsProvenanceReader.ReadCatalog(CatalogPath).Catalog;
        return new NativeMacOsBuildReceiptRequest(
            CatalogPath,
            ArtifactDirectory,
            ReceiptPath,
            RepositoryRoot,
            GhosttyRoot,
            External.ZigArchive,
            External.ZigExecutable,
            EvidenceDirectory,
            EvidenceDirectory,
            EvidenceDirectory,
            External.ZigExecutable,
            EvidenceDirectory,
            External.Clang,
            EvidenceDirectory,
            External.SdkSettings,
            External.ReleaseArchive,
            External.ReleaseArm64,
            External.Metallib,
            Path.Combine(ArtifactDirectory, "libghostty.dylib"),
            catalog.Toolchain.ZigVersion,
            catalog.Build.GhosttyCommit,
            catalog.Build.GhosttyTag,
            catalog.Toolchain.ClangVersion,
            catalog.Toolchain.SdkVersion,
            catalog.Toolchain.SdkBuild,
            catalog.Build.GhosttyOptions,
            catalog.Build.ShimCompilerOptions);
    }
}

internal static class NativeMacOsTestProvenanceCopy
{
    public static void CopyPayload(
        string publish,
        string executable,
        string licenses,
        string catalog,
        string receipt)
    {
        foreach (var name in new[]
                 {
                     "libghostshell-ghostty.dylib",
                     "libghostty.dylib",
                 })
        {
            File.Copy(
                Path.Combine(publish, name),
                Path.Combine(executable, name));
        }

        CopyTree(publish, executable, "ghostty");
        CopyTree(publish, executable, "terminfo");
        File.Copy(
            Path.Combine(publish, "GHOSTTY-LICENSE"),
            Path.Combine(licenses, "GHOSTTY-LICENSE"));
        var native = Path.Combine(licenses, "Native");
        Directory.CreateDirectory(native);
        File.Copy(
            catalog,
            Path.Combine(native, NativeMacOsProvenanceSchema.CatalogFileName));
        File.Copy(
            receipt,
            Path.Combine(native, NativeMacOsProvenanceSchema.ReceiptFileName));
    }

    private static void CopyTree(string sourceRoot, string targetRoot, string name)
    {
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(sourceRoot, name),
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
