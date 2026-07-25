using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhostShell.Packaging;

internal static class NativeMacOsProvenanceReader
{
    private static readonly HashSet<string> RepositoryInputKinds =
    [
        "repository-file",
        "ghostty-build-evidence-file",
        "ghostty-source-file",
        "observed-ghostty-build-evidence",
        "observed-ghostty-resource-evidence",
    ];

    private static readonly HashSet<string> DownloadInputKinds =
    [
        "ghostty-release-archive",
        "zig-archive",
    ];

    private static readonly HashSet<string> DerivedInputKinds =
    [
        "ghostty-metallib",
        "ghostty-release-arm64-binary",
        "zig-executable",
    ];

    private static readonly HashSet<string> BuildHostInputKinds =
    [
        "clang-executable",
        "sdk-settings",
    ];

    private static readonly HashSet<string> MissingEvidenceStatuses =
    [
        "missing",
        "not-applicable",
        "not-packaged",
        "unverified",
    ];

    private static readonly HashSet<string> EvidenceStatuses =
    [
        "missing",
        "not-applicable",
        "not-packaged",
        "packaged",
        "reviewed-not-packaged",
        "unverified",
    ];

    private static readonly HashSet<string> InclusionKinds =
    [
        "build-tool",
        "compiled-input",
        "embedded-binary",
        "embedded-resource",
        "linked-static",
        "packaged-native",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = NativeMacOsProvenanceSchema.MaximumJsonDepth,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static NativeMacOsCatalogDocument ReadCatalog(string path)
    {
        var rawContent = ReadBoundedRegularFile(
            path,
            NativeMacOsProvenanceSchema.MaximumCatalogBytes,
            "native component catalog");
        return ParseCatalog(rawContent);
    }

    public static NativeMacOsCatalogDocument ParseCatalog(byte[] rawContent)
    {
        ArgumentNullException.ThrowIfNull(rawContent);
        if (rawContent.Length == 0
            || rawContent.LongLength > NativeMacOsProvenanceSchema.MaximumCatalogBytes)
        {
            throw new InvalidDataException(
                "The native component catalog has an invalid byte length.");
        }

        var dto = ParseStrict<NativeCatalogDto>(
            rawContent,
            "native component catalog");
        var catalog = ConvertCatalog(dto);
        var sha256 = Convert.ToHexString(SHA256.HashData(rawContent))
            .ToLowerInvariant();
        return new NativeMacOsCatalogDocument(catalog, rawContent, sha256);
    }

    public static NativeMacOsBuildReceiptDocument ReadReceipt(string path)
    {
        var rawContent = ReadBoundedRegularFile(
            path,
            NativeMacOsProvenanceSchema.MaximumReceiptBytes,
            "native build receipt");
        return ParseReceipt(rawContent);
    }

    public static NativeMacOsBuildReceiptDocument ParseReceipt(byte[] rawContent)
    {
        ArgumentNullException.ThrowIfNull(rawContent);
        if (rawContent.Length == 0
            || rawContent.LongLength > NativeMacOsProvenanceSchema.MaximumReceiptBytes)
        {
            throw new InvalidDataException(
                "The native build receipt has an invalid byte length.");
        }

        var dto = ParseStrict<NativeReceiptDto>(
            rawContent,
            "native build receipt");
        return new NativeMacOsBuildReceiptDocument(
            ConvertReceipt(dto),
            rawContent);
    }

    private static T ParseStrict<T>(byte[] rawContent, string label)
    {
        try
        {
            using var document = JsonDocument.Parse(
                rawContent,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = NativeMacOsProvenanceSchema.MaximumJsonDepth,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{label} root must be an object.");
            }

            ValidateJsonShape(document.RootElement);
            return JsonSerializer.Deserialize<T>(rawContent, SerializerOptions)
                ?? throw new InvalidDataException($"{label} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is not valid strict JSON.", exception);
        }
    }

    private static void ValidateJsonShape(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException(
                "Native provenance JSON cannot contain null values.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Duplicate JSON property {property.Name}.");
                }

                ValidateJsonShape(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateJsonShape(item);
            }
        }
    }

    private static NativeMacOsCatalog ConvertCatalog(NativeCatalogDto dto)
    {
        RequireSchemaVersion(dto.SchemaVersion, "catalog");
        RequireExact(
            dto.ReceiptGenerator,
            NativeMacOsProvenanceSchema.Generator,
            "receiptGenerator");
        RequireExact(
            dto.ReleaseReadiness,
            NativeMacOsProvenanceSchema.ReleaseReadiness,
            "releaseReadiness");
        if (dto.LegalClearance is not false)
        {
            throw new InvalidDataException(
                "The reviewed native catalog must state legalClearance false.");
        }

        RequireExact(
            dto.LegalConclusion,
            NativeMacOsProvenanceSchema.LegalConclusion,
            "legalConclusion");
        var catalogId = RequireIdentifier(dto.CatalogId, "catalogId");
        var target = ConvertTarget(Require(dto.Target, "target"));
        var blockers = ConvertBlockers(
            Require(dto.ReleaseBlockers, "releaseBlockers"));
        var inputs = ConvertInputs(Require(dto.Inputs, "inputs"));
        var toolchain = ConvertToolchain(Require(dto.Toolchain, "toolchain"));
        var build = ConvertBuild(Require(dto.Build, "build"));
        var expectedArtifactManifestSha256 = RequireSha256(
            dto.ExpectedArtifactManifestSha256,
            "expectedArtifactManifestSha256");
        var expectedPackagedPayloadManifestSha256 = RequireSha256(
            dto.ExpectedPackagedPayloadManifestSha256,
            "expectedPackagedPayloadManifestSha256");
        var components = ConvertComponents(
            Require(dto.Components, "components"),
            inputs,
            blockers);
        ValidateComponentGraph(components);
        ValidateInputBindings(inputs, toolchain, build);

        return new NativeMacOsCatalog(
            catalogId,
            target,
            NativeMacOsProvenanceSchema.ReleaseReadiness,
            LegalClearance: false,
            NativeMacOsProvenanceSchema.LegalConclusion,
            blockers,
            inputs,
            toolchain,
            build,
            expectedArtifactManifestSha256,
            expectedPackagedPayloadManifestSha256,
            components);
    }

    private static NativeMacOsBuildReceipt ConvertReceipt(NativeReceiptDto dto)
    {
        RequireSchemaVersion(dto.SchemaVersion, "receipt");
        RequireExact(
            dto.Generator,
            NativeMacOsProvenanceSchema.Generator,
            "generator");
        RequireExact(
            dto.ReleaseReadiness,
            NativeMacOsProvenanceSchema.ReleaseReadiness,
            "releaseReadiness");
        if (dto.LegalClearance is not false)
        {
            throw new InvalidDataException(
                "The native build receipt must state legalClearance false.");
        }

        RequireExact(
            dto.LegalConclusion,
            NativeMacOsProvenanceSchema.LegalConclusion,
            "legalConclusion");
        var catalogReference = Require(dto.Catalog, "catalog");
        var catalogId = RequireIdentifier(catalogReference.Id, "catalog.id");
        var catalogSha256 = RequireSha256(
            catalogReference.Sha256,
            "catalog.sha256");
        var target = ConvertTarget(Require(dto.Target, "target"));
        var toolchain = ConvertToolchain(Require(dto.Toolchain, "toolchain"));
        var build = ConvertBuild(Require(dto.Build, "build"));
        var inputs = ConvertReceiptInputs(Require(dto.Inputs, "inputs"));
        var artifactFiles = ConvertArtifactFiles(
            Require(dto.ArtifactFiles, "artifactFiles"));
        var artifactManifest = NativeMacOsArtifactManifest.Create(artifactFiles);
        var packagedManifest = artifactManifest.PackageFiles();

        RequireCount(
            dto.ArtifactFileCount,
            artifactManifest.FileCount,
            "artifactFileCount");
        RequireCount(
            dto.ArtifactBytes,
            artifactManifest.TotalBytes,
            "artifactBytes");
        RequireExact(
            dto.ArtifactManifestSha256,
            artifactManifest.ManifestSha256,
            "artifactManifestSha256");
        RequireCount(
            dto.PackagedFileCount,
            packagedManifest.FileCount,
            "packagedFileCount");
        RequireCount(
            dto.PackagedBytes,
            packagedManifest.TotalBytes,
            "packagedBytes");
        RequireExact(
            dto.PackagedPayloadManifestSha256,
            packagedManifest.ManifestSha256,
            "packagedPayloadManifestSha256");
        ValidateReceiptShape(artifactFiles);

        var componentIds = ConvertIdentifierList(
            Require(dto.ComponentIds, "componentIds"),
            NativeMacOsProvenanceSchema.MaximumComponents,
            "componentIds",
            requireNonEmpty: true);
        var blockerIds = ConvertIdentifierList(
            Require(dto.ReleaseBlockerIds, "releaseBlockerIds"),
            NativeMacOsProvenanceSchema.MaximumBlockers,
            "releaseBlockerIds",
            requireNonEmpty: true);
        return new NativeMacOsBuildReceipt(
            NativeMacOsProvenanceSchema.Generator,
            catalogId,
            catalogSha256,
            target,
            NativeMacOsProvenanceSchema.ReleaseReadiness,
            LegalClearance: false,
            NativeMacOsProvenanceSchema.LegalConclusion,
            toolchain,
            build,
            inputs,
            artifactFiles,
            artifactManifest.FileCount,
            artifactManifest.TotalBytes,
            artifactManifest.ManifestSha256,
            packagedManifest.FileCount,
            packagedManifest.TotalBytes,
            packagedManifest.ManifestSha256,
            componentIds,
            blockerIds);
    }

    private static NativeMacOsTarget ConvertTarget(NativeTargetDto dto)
    {
        RequireExact(dto.Os, "macos", "target.os");
        RequireExact(dto.Architecture, "arm64", "target.architecture");
        RequireExact(dto.MinimumVersion, "13.0", "target.minimumVersion");
        return new NativeMacOsTarget("macos", "arm64", "13.0");
    }

    private static IReadOnlyList<NativeMacOsReleaseBlocker> ConvertBlockers(
        IReadOnlyList<NativeBlockerDto> dtos)
    {
        RequireCollectionSize(
            dtos.Count,
            NativeMacOsProvenanceSchema.MaximumBlockers,
            "releaseBlockers",
            requireNonEmpty: true);
        var blockers = dtos
            .Select(dto => new NativeMacOsReleaseBlocker(
                RequireIdentifier(dto.Id, "releaseBlockers.id"),
                RequireText(dto.Summary, "releaseBlockers.summary", 512)))
            .ToArray();
        RequireSortedUnique(
            blockers,
            blocker => blocker.Id,
            "releaseBlockers");
        return blockers;
    }

    private static IReadOnlyList<NativeMacOsInput> ConvertInputs(
        IReadOnlyList<NativeInputDto> dtos)
    {
        RequireCollectionSize(
            dtos.Count,
            NativeMacOsProvenanceSchema.MaximumInputs,
            "inputs",
            requireNonEmpty: true);
        var inputs = new List<NativeMacOsInput>(dtos.Count);
        foreach (var dto in dtos)
        {
            var id = RequireIdentifier(dto.Id, "inputs.id");
            var kind = RequireIdentifier(dto.Kind, "inputs.kind");
            var locatorCount = new[]
            {
                dto.RepositoryRelativePath is not null,
                dto.DownloadLocation is not null,
                dto.DerivedFromInputId is not null || dto.Derivation is not null,
                dto.BuildHostLogicalPath is not null,
            }.Count(isPresent => isPresent);
            if (locatorCount != 1)
            {
                throw new InvalidDataException(
                    $"Native input {id} must have exactly one reviewed locator.");
            }

            string? repositoryRelativePath = null;
            string? downloadLocation = null;
            string? derivedFromInputId = null;
            string? derivation = null;
            string? buildHostLogicalPath = null;
            if (RepositoryInputKinds.Contains(kind))
            {
                if (dto.RepositoryRelativePath is null)
                {
                    throw new InvalidDataException(
                        $"Native input {id} requires a repository-relative path.");
                }

                repositoryRelativePath = dto.RepositoryRelativePath!;
                NativeMacOsPath.Validate(repositoryRelativePath);
            }
            else if (DownloadInputKinds.Contains(kind))
            {
                if (dto.DownloadLocation is null)
                {
                    throw new InvalidDataException(
                        $"Native input {id} requires an HTTPS download location.");
                }

                downloadLocation = RequireHttpsUri(
                    dto.DownloadLocation!,
                    $"inputs[{id}].downloadLocation");
            }
            else if (DerivedInputKinds.Contains(kind))
            {
                if (dto.DerivedFromInputId is null || dto.Derivation is null)
                {
                    throw new InvalidDataException(
                        $"Native input {id} requires a complete derivation.");
                }

                derivedFromInputId = RequireIdentifier(
                    dto.DerivedFromInputId,
                    $"inputs[{id}].derivedFromInputId");
                derivation = RequireText(
                    dto.Derivation,
                    $"inputs[{id}].derivation",
                    256);
                RejectLocalPath(
                    derivation,
                    $"inputs[{id}].derivation");
            }
            else if (BuildHostInputKinds.Contains(kind))
            {
                if (dto.BuildHostLogicalPath is null)
                {
                    throw new InvalidDataException(
                        $"Native input {id} requires a build-host logical path.");
                }

                buildHostLogicalPath = RequireText(
                    dto.BuildHostLogicalPath,
                    $"inputs[{id}].buildHostLogicalPath",
                    256);
                var expectedLogicalPath = kind switch
                {
                    "clang-executable" => "xcrun:clang",
                    "sdk-settings" => "sdk:MacOSX15.4.sdk/SDKSettings.json",
                    _ => throw new InvalidDataException(
                        $"Native input {id} has an unsupported build-host origin."),
                };
                if (buildHostLogicalPath != expectedLogicalPath)
                {
                    throw new InvalidDataException(
                        $"Native input {id} has an invalid build-host logical path.");
                }
            }
            else
            {
                throw new InvalidDataException(
                    $"Native input {id} has unsupported kind {kind}.");
            }

            inputs.Add(new NativeMacOsInput(
                id,
                kind,
                repositoryRelativePath,
                downloadLocation,
                derivedFromInputId,
                derivation,
                buildHostLogicalPath,
                RequireSha256(dto.Sha256, $"inputs[{id}].sha256")));
        }

        RequireSortedUnique(inputs, input => input.Id, "inputs");
        foreach (var onePerBuildInput in DownloadInputKinds
                     .Concat(DerivedInputKinds)
                     .Concat(BuildHostInputKinds))
        {
            if (inputs.Count(input => input.Kind == onePerBuildInput) != 1)
            {
                throw new InvalidDataException(
                    $"The native catalog must contain exactly one {onePerBuildInput} input.");
            }
        }

        ValidateInputDerivations(inputs);
        return inputs;
    }

    private static void ValidateInputDerivations(
        IReadOnlyList<NativeMacOsInput> inputs)
    {
        var byId = inputs.ToDictionary(input => input.Id, StringComparer.Ordinal);
        foreach (var input in inputs.Where(input =>
                     input.DerivedFromInputId is not null))
        {
            if (input.DerivedFromInputId == input.Id
                || !byId.ContainsKey(input.DerivedFromInputId!))
            {
                throw new InvalidDataException(
                    $"Native input {input.Id} has an invalid derivation source.");
            }

            var parent = byId[input.DerivedFromInputId!];
            var validDerivation = input.Kind switch
            {
                "zig-executable" =>
                    parent.Kind == "zig-archive"
                    && input.Derivation!.StartsWith(
                        "tar-xz-entry:",
                        StringComparison.Ordinal),
                "ghostty-release-arm64-binary" =>
                    parent.Kind == "ghostty-release-archive"
                    && input.Derivation == "mach-o-universal-slice:arm64",
                "ghostty-metallib" =>
                    parent.Kind == "ghostty-release-arm64-binary"
                    && input.Derivation!.StartsWith(
                        "byte-range:",
                        StringComparison.Ordinal),
                _ => false,
            };
            if (!validDerivation)
            {
                throw new InvalidDataException(
                    $"Native input {input.Id} has a mismatched derivation kind.");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            Visit(input.Id);
        }

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new InvalidDataException(
                    $"The native input derivation graph contains a cycle at {id}.");
            }

            if (byId[id].DerivedFromInputId is { } parent)
            {
                Visit(parent);
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static void ValidateInputBindings(
        IReadOnlyList<NativeMacOsInput> inputs,
        NativeMacOsToolchain toolchain,
        NativeMacOsBuildConfiguration build)
    {
        RequireInputHash("zig-archive", toolchain.ZigArchiveSha256);
        RequireInputHash("zig-executable", toolchain.ZigExecutableSha256);
        RequireInputHash("clang-executable", toolchain.ClangSha256);
        RequireInputHash("sdk-settings", toolchain.SdkSettingsSha256);
        RequireInputHash(
            "ghostty-release-archive",
            build.Metallib.ReleaseArchiveSha256);
        RequireInputHash(
            "ghostty-release-arm64-binary",
            build.Metallib.Arm64SliceSha256);
        RequireInputHash("ghostty-metallib", build.Metallib.Sha256);
        var metallibInput = inputs.Single(input =>
            input.Kind == "ghostty-metallib");
        if (metallibInput.Derivation
            != $"byte-range:{build.Metallib.Offset.ToString(CultureInfo.InvariantCulture)}:"
            + build.Metallib.Length.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException(
                "The metallib input derivation does not match its typed byte range.");
        }

        var sdkInput = inputs.Single(input => input.Kind == "sdk-settings");
        if (sdkInput.BuildHostLogicalPath
            != $"sdk:{toolchain.SdkVersion}/SDKSettings.json")
        {
            throw new InvalidDataException(
                "The SDK settings logical origin does not match the SDK identity.");
        }

        void RequireInputHash(string kind, string expectedSha256)
        {
            var input = inputs.Single(item => item.Kind == kind);
            if (input.Sha256 != expectedSha256)
            {
                throw new InvalidDataException(
                    $"Native {kind} input hash does not match its typed build field.");
            }
        }
    }

    private static NativeMacOsToolchain ConvertToolchain(NativeToolchainDto dto)
    {
        var zigVersion = RequireToken(dto.ZigVersion, "toolchain.zigVersion", 64);
        var clangVersion = RequireText(
            dto.ClangVersion,
            "toolchain.clangVersion",
            256);
        RejectLocalPath(clangVersion, "toolchain.clangVersion");
        var sdkVersion = RequireToken(dto.SdkVersion, "toolchain.sdkVersion", 128);
        var sdkBuild = dto.SdkBuild is null
            ? null
            : RequireToken(dto.SdkBuild, "toolchain.sdkBuild", 128);
        return new NativeMacOsToolchain(
            zigVersion,
            RequireSha256(
                dto.ZigArchiveSha256,
                "toolchain.zigArchiveSha256"),
            RequireSha256(
                dto.ZigExecutableSha256,
                "toolchain.zigExecutableSha256"),
            clangVersion,
            RequireSha256(dto.ClangSha256, "toolchain.clangSha256"),
            sdkVersion,
            sdkBuild,
            RequireSha256(
                dto.SdkSettingsSha256,
                "toolchain.sdkSettingsSha256"));
    }

    private static NativeMacOsBuildConfiguration ConvertBuild(NativeBuildDto dto)
    {
        var commit = RequireLowerHex(
            dto.GhosttyCommit,
            40,
            "build.ghosttyCommit");
        var tag = RequireToken(dto.GhosttyTag, "build.ghosttyTag", 64);
        var ghosttyOptions = ConvertOptions(
            Require(dto.GhosttyOptions, "build.ghosttyOptions"),
            "build.ghosttyOptions");
        var shimCompilerOptions = ConvertOptions(
            Require(dto.ShimCompilerOptions, "build.shimCompilerOptions"),
            "build.shimCompilerOptions");
        var metallibDto = Require(dto.Metallib, "build.metallib");
        var offset = RequireNonNegative(
            metallibDto.Offset,
            "build.metallib.offset");
        var length = RequirePositive(
            metallibDto.Length,
            "build.metallib.length");
        return new NativeMacOsBuildConfiguration(
            commit,
            tag,
            ghosttyOptions,
            shimCompilerOptions,
            new NativeMacOsMetallib(
                RequireSha256(
                    metallibDto.ReleaseArchiveSha256,
                    "build.metallib.releaseArchiveSha256"),
                RequireSha256(
                    metallibDto.Arm64SliceSha256,
                    "build.metallib.arm64SliceSha256"),
                offset,
                length,
                RequireSha256(
                    metallibDto.Sha256,
                    "build.metallib.sha256")));
    }

    private static IReadOnlyList<string> ConvertOptions(
        IReadOnlyList<string> values,
        string label)
    {
        RequireCollectionSize(values.Count, 128, label, requireNonEmpty: true);
        return values
            .Select(value =>
            {
                var option = RequireText(value, label, 256);
                RejectLocalPath(option, label);
                return option;
            })
            .ToArray();
    }

    private static IReadOnlyList<NativeMacOsComponent> ConvertComponents(
        IReadOnlyList<NativeComponentDto> dtos,
        IReadOnlyList<NativeMacOsInput> inputs,
        IReadOnlyList<NativeMacOsReleaseBlocker> blockers)
    {
        RequireCollectionSize(
            dtos.Count,
            NativeMacOsProvenanceSchema.MaximumComponents,
            "components",
            requireNonEmpty: true);
        var inputIds = inputs.Select(input => input.Id)
            .ToHashSet(StringComparer.Ordinal);
        var inputsById = inputs.ToDictionary(input => input.Id, StringComparer.Ordinal);
        var blockerIds = blockers.Select(blocker => blocker.Id)
            .ToHashSet(StringComparer.Ordinal);
        var components = new List<NativeMacOsComponent>(dtos.Count);
        foreach (var dto in dtos)
        {
            var id = RequireIdentifier(dto.Id, "components.id");
            var selectionEvidenceInputId = RequireIdentifier(
                dto.SelectionEvidenceInputId,
                $"components[{id}].selectionEvidenceInputId");
            if (!inputIds.Contains(selectionEvidenceInputId))
            {
                throw new InvalidDataException(
                    $"Component {id} refers to unknown selection evidence "
                    + $"{selectionEvidenceInputId}.");
            }
            var selectionEvidenceInput =
                inputsById[selectionEvidenceInputId];

            var evidenceInputIds = ConvertIdentifierList(
                Require(
                    dto.LicenseEvidenceInputIds,
                    $"components[{id}].licenseEvidenceInputIds"),
                NativeMacOsProvenanceSchema.MaximumInputs,
                $"components[{id}].licenseEvidenceInputIds",
                requireNonEmpty: false);
            if (evidenceInputIds.Any(inputId => !inputIds.Contains(inputId)))
            {
                throw new InvalidDataException(
                    $"Component {id} refers to unknown license evidence input.");
            }

            if (evidenceInputIds.Any(inputId =>
                    inputsById[inputId].Kind is not (
                "repository-file" or "ghostty-source-file")))
            {
                throw new InvalidDataException(
                    $"Component {id} cites a non-source input as license evidence.");
            }

            var componentBlockerIds = ConvertIdentifierList(
                Require(dto.BlockerIds, $"components[{id}].blockerIds"),
                NativeMacOsProvenanceSchema.MaximumBlockers,
                $"components[{id}].blockerIds",
                requireNonEmpty: false);
            if (componentBlockerIds.Any(blockerId => !blockerIds.Contains(blockerId)))
            {
                throw new InvalidDataException(
                    $"Component {id} refers to an unknown release blocker.");
            }

            var evidenceStatus = RequireIdentifier(
                dto.LicenseEvidenceStatus,
                $"components[{id}].licenseEvidenceStatus");
            if (evidenceInputIds.Count == 0
                && (!MissingEvidenceStatuses.Contains(evidenceStatus)
                    || componentBlockerIds.Count == 0))
            {
                throw new InvalidDataException(
                    $"Component {id} lacks explicit missing license evidence and blockers.");
            }

            if (!EvidenceStatuses.Contains(evidenceStatus))
            {
                throw new InvalidDataException(
                    $"Component {id} has unsupported license evidence status.");
            }

            var inclusion = RequireIdentifier(
                dto.Inclusion,
                $"components[{id}].inclusion");
            if (!InclusionKinds.Contains(inclusion))
            {
                throw new InvalidDataException(
                    $"Component {id} has unsupported selected inclusion kind.");
            }

            var validSelectionEvidence = inclusion switch
            {
                "build-tool" => selectionEvidenceInput.Kind is
                    "sdk-settings" or "zig-archive",
                "linked-static" or "compiled-input" =>
                    selectionEvidenceInput.Kind is
                        "ghostty-build-evidence-file"
                        or "observed-ghostty-build-evidence",
                "packaged-native" => selectionEvidenceInput.Kind is
                    "repository-file"
                    or "ghostty-build-evidence-file"
                    or "observed-ghostty-build-evidence",
                "embedded-resource" =>
                    selectionEvidenceInput.Kind is
                        "ghostty-build-evidence-file"
                        or "ghostty-source-file"
                        or "observed-ghostty-build-evidence"
                        or "observed-ghostty-resource-evidence",
                "embedded-binary" => selectionEvidenceInput.Kind is
                    "ghostty-build-evidence-file"
                    or "observed-ghostty-build-evidence"
                    or "ghostty-metallib",
                _ => false,
            };
            if (!validSelectionEvidence)
            {
                throw new InvalidDataException(
                    $"Component {id} has mismatched selection evidence.");
            }

            var dependencies = ConvertIdentifierList(
                Require(
                    dto.DependsOnComponentIds,
                    $"components[{id}].dependsOnComponentIds"),
                NativeMacOsProvenanceSchema.MaximumComponents,
                $"components[{id}].dependsOnComponentIds",
                requireNonEmpty: false);
            components.Add(new NativeMacOsComponent(
                id,
                RequireText(dto.Name, $"components[{id}].name", 256),
                RequireToken(dto.Version, $"components[{id}].version", 128),
                inclusion,
                RequireText(
                    dto.InclusionBasis,
                    $"components[{id}].inclusionBasis",
                    512),
                selectionEvidenceInputId,
                RequireText(
                    dto.LicenseDeclared,
                    $"components[{id}].licenseDeclared",
                    128),
                evidenceStatus,
                evidenceInputIds,
                componentBlockerIds,
                dependencies));
        }

        RequireSortedUnique(components, component => component.Id, "components");
        return components;
    }

    private static void ValidateComponentGraph(
        IReadOnlyList<NativeMacOsComponent> components)
    {
        const string rootId = "ghostshell-ghostty-shim";
        var byId = components.ToDictionary(
            component => component.Id,
            StringComparer.Ordinal);
        if (!byId.ContainsKey(rootId))
        {
            throw new InvalidDataException(
                $"The native component graph is missing root {rootId}.");
        }

        foreach (var component in components)
        {
            foreach (var dependencyId in component.DependsOnComponentIds)
            {
                if (dependencyId == component.Id)
                {
                    throw new InvalidDataException(
                        $"Component {component.Id} cannot depend on itself.");
                }

                if (!byId.ContainsKey(dependencyId))
                {
                    throw new InvalidDataException(
                        $"Component {component.Id} depends on unknown component {dependencyId}.");
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(rootId);
        if (visited.Count != components.Count)
        {
            var unreachable = components
                .Select(component => component.Id)
                .Where(id => !visited.Contains(id))
                .Order(StringComparer.Ordinal)
                .First();
            throw new InvalidDataException(
                $"Native component {unreachable} is unreachable from {rootId}.");
        }

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new InvalidDataException(
                    $"The native component graph contains a cycle at {id}.");
            }

            foreach (var dependency in byId[id].DependsOnComponentIds)
            {
                Visit(dependency);
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static IReadOnlyList<NativeMacOsReceiptInput> ConvertReceiptInputs(
        IReadOnlyList<NativeReceiptInputDto> dtos)
    {
        RequireCollectionSize(
            dtos.Count,
            NativeMacOsProvenanceSchema.MaximumInputs,
            "inputs",
            requireNonEmpty: true);
        var inputs = dtos.Select(dto => new NativeMacOsReceiptInput(
                RequireIdentifier(dto.Id, "inputs.id"),
                RequireSha256(dto.Sha256, "inputs.sha256")))
            .ToArray();
        RequireSortedUnique(inputs, input => input.Id, "inputs");
        return inputs;
    }

    private static IReadOnlyList<NativeMacOsArtifactFile> ConvertArtifactFiles(
        IReadOnlyList<NativeArtifactFileDto> dtos)
    {
        RequireCollectionSize(
            dtos.Count,
            NativeMacOsProvenanceSchema.MaximumArtifactFiles,
            "artifactFiles",
            requireNonEmpty: true);
        var files = new List<NativeMacOsArtifactFile>(dtos.Count);
        long cumulativeBytes = 0;
        foreach (var dto in dtos)
        {
            var path = RequireText(dto.Path, "artifactFiles.path", 240);
            NativeMacOsPath.Validate(path);
            var role = RequireIdentifier(dto.Role, $"artifactFiles[{path}].role");
            var expectedRole =
                NativeMacOsArtifactManifestInspector.ClassifyArtifactPath(path);
            if (role != expectedRole)
            {
                throw new InvalidDataException(
                    $"Native receipt file {path} has incorrect role {role}.");
            }

            var length = RequireNonNegative(
                dto.Length,
                $"artifactFiles[{path}].length");
            try
            {
                cumulativeBytes = checked(cumulativeBytes + length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "The native receipt artifact byte count overflowed.",
                    exception);
            }

            if (cumulativeBytes > NativeMacOsProvenanceSchema.MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    "The native receipt exceeds the artifact byte limit.");
            }

            var unixMode = RequireText(
                dto.UnixMode,
                $"artifactFiles[{path}].unixMode",
                4);
            if (unixMode.Length != 4
                || unixMode.Any(character => character is < '0' or > '7'))
            {
                throw new InvalidDataException(
                    $"Native receipt file {path} has invalid Unix mode.");
            }

            files.Add(new NativeMacOsArtifactFile(
                path,
                role,
                length,
                unixMode,
                RequireSha256(
                    dto.Sha256,
                    $"artifactFiles[{path}].sha256")));
        }

        RequireSortedUnique(files, file => file.Path, "artifactFiles");
        NativeMacOsPath.ValidatePortableUniqueness(files.Select(file => file.Path));
        return files;
    }

    private static void ValidateReceiptShape(
        IReadOnlyList<NativeMacOsArtifactFile> files)
    {
        var paths = files.Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "GHOSTTY-LICENSE",
                     "ghostshell-ghostty-smoke",
                     "libghostshell-ghostty.dylib",
                     "libghostty.dylib",
                 })
        {
            if (!paths.Contains(required))
            {
                throw new InvalidDataException(
                    $"The native build receipt is missing {required}.");
            }
        }

        if (!files.Any(file =>
                file.Path.StartsWith("ghostty/", StringComparison.Ordinal))
            || !files.Any(file =>
                file.Path.StartsWith("terminfo/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The native build receipt is missing packaged resources.");
        }
    }

    private static IReadOnlyList<string> ConvertIdentifierList(
        IReadOnlyList<string> values,
        int maximum,
        string label,
        bool requireNonEmpty)
    {
        RequireCollectionSize(values.Count, maximum, label, requireNonEmpty);
        var identifiers = values
            .Select(value => RequireIdentifier(value, label))
            .ToArray();
        RequireSortedUnique(identifiers, value => value, label);
        return identifiers;
    }

    private static byte[] ReadBoundedRegularFile(
        string path,
        long maximumBytes,
        string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = RegularPackageFileReader.Open(path, out var inspection);
        if (inspection.Length <= 0 || inspection.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {label} has an invalid byte length.");
        }

        var content = GC.AllocateUninitializedArray<byte>(
            checked((int)inspection.Length));
        var offset = 0;
        while (offset < content.Length)
        {
            var read = stream.Read(content, offset, content.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"The {label} became shorter while it was read.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"The {label} became longer while it was read.");
        }

        return content;
    }

    private static T Require<T>(T? value, string label)
        where T : class =>
        value ?? throw new InvalidDataException($"{label} is required.");

    private static void RequireSchemaVersion(int? version, string label)
    {
        if (version != NativeMacOsProvenanceSchema.Version)
        {
            throw new InvalidDataException(
                $"The native {label} schemaVersion must be "
                + $"{NativeMacOsProvenanceSchema.Version}.");
        }
    }

    private static string RequireIdentifier(string? value, string label)
    {
        var identifier = RequireText(value, label, 128);
        if (identifier[0] is < 'a' or > 'z'
            || identifier.Any(character =>
                character is not (
                    >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-'
                    or '.')))
        {
            throw new InvalidDataException(
                $"{label} must be a lowercase stable identifier.");
        }

        return identifier;
    }

    private static string RequireToken(string? value, string label, int maximumLength)
    {
        var token = RequireText(value, label, maximumLength);
        if (token.Contains('/')
            || token.Contains('\\')
            || token.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException($"{label} must be a path-free token.");
        }

        return token;
    }

    private static string RequireText(string? value, string label, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"{label} has invalid text.");
        }

        return value;
    }

    private static string RequireSha256(string? value, string label) =>
        RequireLowerHex(value, 64, label);

    private static string RequireLowerHex(
        string? value,
        int length,
        string label)
    {
        if (value is null
            || value.Length != length
            || value.Any(character =>
                character is not (
                    >= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{label} must be {length} lowercase hexadecimal characters.");
        }

        return value;
    }

    private static string RequireHttpsUri(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidDataException(
                $"{label} must be an absolute credential-free HTTPS URL.");
        }

        return uri.AbsoluteUri;
    }

    private static long RequireNonNegative(long? value, string label)
    {
        if (value is null or < 0)
        {
            throw new InvalidDataException($"{label} must be non-negative.");
        }

        return value.Value;
    }

    private static long RequirePositive(long? value, string label)
    {
        if (value is null or <= 0)
        {
            throw new InvalidDataException($"{label} must be positive.");
        }

        return value.Value;
    }

    private static void RequireCount(long? actual, long expected, string label)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"{label} does not match the receipt file manifest.");
        }
    }

    private static void RequireExact(
        string? actual,
        string expected,
        string label)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} must be {expected}.");
        }
    }

    private static void RequireCollectionSize(
        int count,
        int maximum,
        string label,
        bool requireNonEmpty)
    {
        if ((requireNonEmpty && count == 0) || count > maximum)
        {
            throw new InvalidDataException(
                $"{label} has an invalid number of entries.");
        }
    }

    private static void RequireSortedUnique<T>(
        IReadOnlyList<T> items,
        Func<T, string> key,
        string label)
    {
        string? previous = null;
        foreach (var item in items)
        {
            var current = key(item);
            if (previous is not null
                && string.CompareOrdinal(previous, current) >= 0)
            {
                throw new InvalidDataException(
                    $"{label} must be sorted with unique entries.");
            }

            previous = current;
        }
    }

    private static void RejectLocalPath(string value, string label)
    {
        var containsAbsoluteToken = value.StartsWith('/')
            || value.Contains(" /", StringComparison.Ordinal)
            || value.Contains("=/", StringComparison.Ordinal)
            || value.Contains("-I/", StringComparison.Ordinal)
            || value.Contains("-L/", StringComparison.Ordinal);
        if (containsAbsoluteToken
            || value.Contains("/Users/", StringComparison.Ordinal)
            || value.Contains("/home/", StringComparison.Ordinal)
            || value.Contains("/private/", StringComparison.Ordinal)
            || value.Contains("/tmp/", StringComparison.Ordinal)
            || value.Contains("/var/", StringComparison.Ordinal)
            || value.Contains("../", StringComparison.Ordinal)
            || value.Contains("/..", StringComparison.Ordinal)
            || value.Contains("://", StringComparison.Ordinal)
            || value.Contains('\\'))
        {
            throw new InvalidDataException($"{label} cannot contain a local path.");
        }
    }

    private sealed class NativeCatalogDto
    {
        public int? SchemaVersion { get; init; }
        public string? CatalogId { get; init; }
        public string? ReceiptGenerator { get; init; }
        public NativeTargetDto? Target { get; init; }
        public string? ReleaseReadiness { get; init; }
        public bool? LegalClearance { get; init; }
        public string? LegalConclusion { get; init; }
        public NativeBlockerDto[]? ReleaseBlockers { get; init; }
        public NativeInputDto[]? Inputs { get; init; }
        public NativeToolchainDto? Toolchain { get; init; }
        public NativeBuildDto? Build { get; init; }
        public string? ExpectedArtifactManifestSha256 { get; init; }
        public string? ExpectedPackagedPayloadManifestSha256 { get; init; }
        public NativeComponentDto[]? Components { get; init; }
    }

    private sealed class NativeTargetDto
    {
        public string? Os { get; init; }
        public string? Architecture { get; init; }
        public string? MinimumVersion { get; init; }
    }

    private sealed class NativeBlockerDto
    {
        public string? Id { get; init; }
        public string? Summary { get; init; }
    }

    private sealed class NativeInputDto
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? RepositoryRelativePath { get; init; }
        public string? DownloadLocation { get; init; }
        public string? DerivedFromInputId { get; init; }
        public string? Derivation { get; init; }
        public string? BuildHostLogicalPath { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class NativeToolchainDto
    {
        public string? ZigVersion { get; init; }
        public string? ZigArchiveSha256 { get; init; }
        public string? ZigExecutableSha256 { get; init; }
        public string? ClangVersion { get; init; }
        public string? ClangSha256 { get; init; }
        public string? SdkVersion { get; init; }
        public string? SdkBuild { get; init; }
        public string? SdkSettingsSha256 { get; init; }
    }

    private sealed class NativeBuildDto
    {
        public string? GhosttyCommit { get; init; }
        public string? GhosttyTag { get; init; }
        public string[]? GhosttyOptions { get; init; }
        public string[]? ShimCompilerOptions { get; init; }
        public NativeMetallibDto? Metallib { get; init; }
    }

    private sealed class NativeMetallibDto
    {
        public string? ReleaseArchiveSha256 { get; init; }
        public string? Arm64SliceSha256 { get; init; }
        public long? Offset { get; init; }
        public long? Length { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class NativeComponentDto
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Version { get; init; }
        public string? Inclusion { get; init; }
        public string? InclusionBasis { get; init; }
        public string? SelectionEvidenceInputId { get; init; }
        public string? LicenseDeclared { get; init; }
        public string? LicenseEvidenceStatus { get; init; }
        public string[]? LicenseEvidenceInputIds { get; init; }
        public string[]? BlockerIds { get; init; }
        public string[]? DependsOnComponentIds { get; init; }
    }

    private sealed class NativeReceiptDto
    {
        public int? SchemaVersion { get; init; }
        public string? Generator { get; init; }
        public NativeCatalogReferenceDto? Catalog { get; init; }
        public NativeTargetDto? Target { get; init; }
        public string? ReleaseReadiness { get; init; }
        public bool? LegalClearance { get; init; }
        public string? LegalConclusion { get; init; }
        public NativeToolchainDto? Toolchain { get; init; }
        public NativeBuildDto? Build { get; init; }
        public NativeReceiptInputDto[]? Inputs { get; init; }
        public NativeArtifactFileDto[]? ArtifactFiles { get; init; }
        public long? ArtifactFileCount { get; init; }
        public long? ArtifactBytes { get; init; }
        public string? ArtifactManifestSha256 { get; init; }
        public long? PackagedFileCount { get; init; }
        public long? PackagedBytes { get; init; }
        public string? PackagedPayloadManifestSha256 { get; init; }
        public string[]? ComponentIds { get; init; }
        public string[]? ReleaseBlockerIds { get; init; }
    }

    private sealed class NativeCatalogReferenceDto
    {
        public string? Id { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class NativeReceiptInputDto
    {
        public string? Id { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class NativeArtifactFileDto
    {
        public string? Path { get; init; }
        public string? Role { get; init; }
        public long? Length { get; init; }
        public string? UnixMode { get; init; }
        public string? Sha256 { get; init; }
    }
}
