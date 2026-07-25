using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GhostShell.Packaging;

internal sealed record NativeMacOsBuildEvidenceRequest(
    string TracePath,
    string RepositoryRoot,
    string GhosttySourceDirectory,
    string ZigExecutablePath,
    string ZigLibraryDirectory,
    string ZigLocalCacheDirectory,
    string ZigGlobalCacheDirectory,
    string SdkDirectory,
    string MetallibPath,
    string InstallDirectory,
    string ArtifactLibGhosttyPath);

internal sealed record NativeMacOsBuildEvidenceResult(
    byte[] CanonicalJson,
    string Sha256);

/// <summary>
/// Turns one isolated, verbose Zig build into path-independent evidence. The
/// observer never executes trace text; it records every semantic final-command
/// token and verifies every selected manifest record against the filesystem.
/// </summary>
internal static class NativeMacOsBuildEvidenceBuilder
{
    private const int SchemaVersion = 2;
    private const string Generator =
        "GhostShell.Packaging.NativeMacOsBuildEvidence-2.0.0";
    private const string Target = "aarch64-macos.13.0";
    private const string Optimization = "ReleaseFast";
    private const string InstallName = "@rpath/libghostty.dylib";
    private const int MaximumTraceBytes = 8 * 1024 * 1024;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumTraceLines = 100_000;
    private const int MaximumTokensPerLine = 16_384;
    private const int MaximumManifestFiles = 4_096;
    private const int MaximumManifestRecords = 20_000;
    private const int MaximumManifestPathCharacters = 4_096;
    private const long MaximumEvidenceFileBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumSemanticDirectoryEntries = 16_384;
    private const int MaximumSemanticDirectoryDepth = 32;
    private const long MaximumSemanticDirectoryBytes = 512L * 1024 * 1024;
    private const int MaximumSemanticDirectoryTotalEntries = 32_768;
    private const long MaximumSemanticDirectoryTotalBytes =
        1024L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] ExpectedArchiveNames =
    [
        "libdcimgui.a",
        "libfreetype.a",
        "libglslang.a",
        "libhighway.a",
        "libintl.a",
        "libmacos.a",
        "liboniguruma.a",
        "libpng.a",
        "libsimdutf.a",
        "libspirv_cross.a",
        "libutfcpp.a",
        "libz.a",
    ];

    public static NativeMacOsBuildEvidenceResult Observe(
        NativeMacOsBuildEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = EvidencePaths.Parse(request);
        var trace = ParseTrace(paths);
        var command = SelectFinalCommand(trace, paths);
        var commandEvidence = ObserveFinalCommand(command, paths);
        var output = ObserveInstalledOutput(trace, paths);
        var manifests = ReadManifests(paths);
        var compileManifest = SelectCompileManifest(
            manifests,
            commandEvidence.ArchivePaths,
            paths);
        var buildGraphManifest = SelectBuildGraphManifest(manifests, paths);
        var compileClosure = ObserveClosure(
            compileManifest,
            paths,
            commandEvidence.ArchivePaths);
        var buildGraphClosure = ObserveClosure(
            buildGraphManifest,
            paths,
            []);
        var canonicalJson = WriteCanonicalJson(
            commandEvidence,
            output,
            compileClosure,
            buildGraphClosure);
        var sha256 = Convert.ToHexString(SHA256.HashData(canonicalJson))
            .ToLowerInvariant();
        return new NativeMacOsBuildEvidenceResult(canonicalJson, sha256);
    }

    // Trace parsing and pinned-command validation

    private static IReadOnlyList<TraceLine> ParseTrace(EvidencePaths paths)
    {
        var lines = ReadBoundedLines(
            paths.TracePath,
            MaximumTraceBytes,
            MaximumTraceLines,
            "native build trace");
        var parsed = new List<TraceLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            parsed.Add(new TraceLine(i + 1, Tokenize(lines[i], i + 1)));
        }

        return parsed;
    }

    private static TraceLine SelectFinalCommand(
        IReadOnlyList<TraceLine> trace,
        EvidencePaths paths)
    {
        var candidates = trace
            .Where(line => IsGhosttyBuildLib(line.Tokens))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidDataException(
                "The native build trace must contain exactly one ghostty build-lib command.");
        }

        var candidate = candidates[0];
        if (candidate.Tokens.Count < 2
            || candidate.Tokens[1] != "build-lib"
            || !PathsEqual(
                ResolveArgumentPath(candidate.Tokens[0], paths.RepositoryRoot),
                paths.ZigExecutablePath))
        {
            throw new InvalidDataException(
                "The ghostty build-lib command does not use the pinned Zig executable.");
        }

        return candidate;
    }

    private static FinalCommandEvidence ObserveFinalCommand(
        TraceLine command,
        EvidencePaths paths)
    {
        var tokens = command.Tokens;
        RequireSingleOptionValue(tokens, "--name", "ghostty");
        RequireFlagCount(tokens, "-dynamic", 1);
        RequireFlagCount(tokens, "-static", 0);
        RequireAllOptionValues(tokens, "-target", Target);
        RequireRootOptimization(tokens);
        RequireSingleOptionValue(tokens, "-install_name", InstallName);
        RequireSinglePathOption(
            tokens,
            "--cache-dir",
            paths.ZigLocalCacheDirectory,
            paths);
        RequireSinglePathOption(
            tokens,
            "--global-cache-dir",
            paths.ZigGlobalCacheDirectory,
            paths);
        RequireSinglePathOption(
            tokens,
            "--zig-lib-dir",
            paths.ZigLibraryDirectory,
            paths);
        RequireAllPathOptions(
            tokens,
            "-iframework",
            Path.Combine(paths.SdkDirectory, "System", "Library", "Frameworks"),
            paths);
        RequireAllPathOptions(
            tokens,
            "-isystem",
            Path.Combine(paths.SdkDirectory, "usr", "include"),
            paths);
        RequireAllPathOptions(
            tokens,
            "-L",
            Path.Combine(paths.SdkDirectory, "usr", "lib"),
            paths);

        var modules = ParseModules(tokens, paths);
        var rootModules = modules
            .Where(module => module.Name == "root")
            .ToArray();
        var expectedRoot = Path.Combine(
            paths.GhosttySourceDirectory,
            "src",
            "main_c.zig");
        if (rootModules.Length != 1
            || !PathsEqual(rootModules[0].Path, expectedRoot))
        {
            throw new InvalidDataException(
                "The ghostty build-lib command has an unexpected root module.");
        }

        var metallibModules = modules
            .Where(module => module.Name == "ghostty_metallib")
            .ToArray();
        if (metallibModules.Length != 1
            || !PathsEqual(metallibModules[0].Path, paths.MetallibPath))
        {
            throw new InvalidDataException(
                "The ghostty build-lib command has unexpected metallib input.");
        }

        var archives = ObserveArchives(tokens, paths);
        var derivedModules = ObserveDerivedModules(modules, paths);
        var frameworks = ReadPairedValues(tokens, "-framework");
        if (frameworks.Count == 0
            || frameworks.Any(value => !IsSafeSemanticName(value)))
        {
            throw new InvalidDataException(
                "The ghostty build-lib command has invalid framework inputs.");
        }

        var systemLibraries = tokens
            .Where(IsSystemLibraryFlag)
            .Select(token => token[2..])
            .ToArray();
        if (systemLibraries.Length == 0
            || systemLibraries.Any(value => !IsSafeSemanticName(value)))
        {
            throw new InvalidDataException(
                "The ghostty build-lib command has invalid system-library inputs.");
        }

        return new FinalCommandEvidence(
            archives.Select(archive => archive.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            archives.Select(archive => archive.Path).ToArray(),
            RequireUniqueSorted(frameworks, "framework"),
            RequireUniqueSorted(systemLibraries, "system library"),
            derivedModules,
            NormalizeSemanticCommand(
                tokens,
                paths,
                archives.Select(archive => archive.Path)
                    .ToHashSet(PathComparer)));
    }

    private static IReadOnlyList<ModuleArgument> ParseModules(
        IReadOnlyList<string> tokens,
        EvidencePaths paths)
    {
        var modules = new List<ModuleArgument>();
        foreach (var token in tokens)
        {
            if (!token.StartsWith("-M", StringComparison.Ordinal))
            {
                continue;
            }

            var equals = token.IndexOf('=', 2);
            if (equals <= 2 || equals == token.Length - 1)
            {
                throw new InvalidDataException(
                    "The ghostty build-lib command has a malformed module argument.");
            }

            var name = token[2..equals];
            if (!IsSafeModuleName(name))
            {
                throw new InvalidDataException(
                    "The ghostty build-lib command has an invalid module name.");
            }

            var path = ResolveArgumentPath(token[(equals + 1)..], paths.RepositoryRoot);
            var isAllowed = IsWithin(path, paths.GhosttySourceDirectory)
                || IsWithin(path, paths.RepositoryRoot)
                || IsWithin(path, paths.ZigGlobalCacheDirectory)
                || IsWithin(path, paths.ZigLocalCacheDirectory)
                || PathsEqual(path, paths.MetallibPath);
            if (!isAllowed)
            {
                throw new InvalidDataException(
                    "The ghostty build-lib command has a module outside its pinned roots.");
            }

            modules.Add(new ModuleArgument(name, path));
        }

        var duplicate = modules
            .GroupBy(module => module.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"The ghostty build-lib command repeats module {duplicate.Key}.");
        }

        return modules;
    }

    private static IReadOnlyList<ArchiveArgument> ObserveArchives(
        IReadOnlyList<string> tokens,
        EvidencePaths paths)
    {
        var archives = new List<ArchiveArgument>();
        foreach (var token in tokens.Where(
                     token => token.EndsWith(".a", StringComparison.Ordinal)))
        {
            var path = ResolveArgumentPath(token, paths.RepositoryRoot);
            if (!IsWithin(path, paths.ZigLocalCacheDirectory))
            {
                throw new InvalidDataException(
                    "The ghostty build-lib command has an archive outside the fresh local cache.");
            }

            ValidateLocalCacheOutputPath(path, paths, Path.GetFileName(path));
            var name = Path.GetFileName(path);
            _ = ObserveRegularFile(
                path,
                paths.ZigLocalCacheDirectory,
                allowSdkLinks: false);
            archives.Add(new ArchiveArgument(name, path));
        }

        var uniqueArchives = new List<ArchiveArgument>();
        foreach (var group in archives.GroupBy(
                     archive => archive.Name,
                     StringComparer.Ordinal))
        {
            var pathsForName = group
                .Select(archive => archive.Path)
                .Distinct(PathComparer)
                .ToArray();
            if (pathsForName.Length != 1)
            {
                throw new InvalidDataException(
                    "An archive basename maps to more than one fresh-cache output.");
            }

            uniqueArchives.Add(new ArchiveArgument(group.Key, pathsForName[0]));
        }

        var names = uniqueArchives
            .Select(archive => archive.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (!names.SequenceEqual(ExpectedArchiveNames, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The ghostty build-lib command does not contain the exact reviewed archive set.");
        }

        return uniqueArchives;
    }

    private static IReadOnlyList<ObservedModule> ObserveDerivedModules(
        IReadOnlyList<ModuleArgument> modules,
        EvidencePaths paths)
    {
        var observed = new List<ObservedModule>();
        foreach (var module in modules.Where(
                     module => IsWithin(
                         module.Path,
                         paths.ZigLocalCacheDirectory)))
        {
            if (module.Path.EndsWith(".a", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A generated module unexpectedly refers to an archive.");
            }

            ValidateLocalCacheDerivedPath(module.Path, paths);
            var file = ObserveRegularFile(
                module.Path,
                paths.ZigLocalCacheDirectory,
                allowSdkLinks: false);
            observed.Add(new ObservedModule(
                module.Name,
                file.Length,
                file.Sha256));
        }

        return observed
            .OrderBy(module => module.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static ObservedFile ObserveInstalledOutput(
        IReadOnlyList<TraceLine> trace,
        EvidencePaths paths)
    {
        var expectedInstalledPath = Path.Combine(
            paths.InstallDirectory,
            "lib",
            "libghostty.dylib");
        var edges = new List<string>();
        foreach (var line in trace.Where(
                     line => line.Tokens.Count > 0
                         && line.Tokens[0] == "install"))
        {
            var tokens = line.Tokens;
            var couldTargetOutput = tokens.Count >= 2
                && TryResolveArgumentPath(
                    tokens[^1],
                    paths.RepositoryRoot,
                    out var destination)
                && PathsEqual(destination, expectedInstalledPath);
            if (!couldTargetOutput)
            {
                continue;
            }

            if (tokens.Count != 4 || tokens[1] != "-C")
            {
                throw new InvalidDataException(
                    "The libghostty install edge has an unexpected command shape.");
            }

            edges.Add(ResolveArgumentPath(tokens[2], paths.RepositoryRoot));
        }

        if (edges.Count != 1)
        {
            throw new InvalidDataException(
                "The native build trace must contain exactly one libghostty install edge.");
        }

        ValidateLocalCacheOutputPath(
            edges[0],
            paths,
            "libghostty.dylib");
        var source = ObserveRegularFile(
            edges[0],
            paths.ZigLocalCacheDirectory,
            allowSdkLinks: false);
        var installed = ObserveRegularFile(
            expectedInstalledPath,
            paths.InstallDirectory,
            allowSdkLinks: false);
        var artifact = ObserveRegularFile(
            paths.ArtifactLibGhosttyPath,
            paths.RepositoryRoot,
            allowSdkLinks: false);
        if (source != installed || source != artifact)
        {
            throw new InvalidDataException(
                "The compiled, installed, and packaged libghostty files are not byte-exact.");
        }

        return artifact;
    }

    // Zig h-manifest selection and normalization

    private static IReadOnlyList<ZigManifest> ReadManifests(EvidencePaths paths)
    {
        var manifestDirectory = Path.Combine(
            paths.ZigLocalCacheDirectory,
            "h");
        RequireDirectory(manifestDirectory, "Zig h-manifest directory");
        RejectLinkTraversal(
            paths.ZigLocalCacheDirectory,
            manifestDirectory,
            includeFinalEntry: false);
        InspectLink(new DirectoryInfo(manifestDirectory));

        var files = new DirectoryInfo(manifestDirectory)
            .EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                })
            .Where(entry => entry.Name.EndsWith(".txt", StringComparison.Ordinal))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0 || files.Length > MaximumManifestFiles)
        {
            throw new InvalidDataException(
                "The fresh Zig cache has an invalid h-manifest count.");
        }

        var manifests = new List<ZigManifest>(files.Length);
        foreach (var entry in files)
        {
            if (entry is not FileInfo
                || entry.LinkTarget is not null
                || !IsLowerHexFileName(entry.Name))
            {
                throw new InvalidDataException(
                    "The fresh Zig cache contains an invalid h-manifest entry.");
            }

            manifests.Add(ParseManifest(
                entry.FullName,
                paths.ZigLocalCacheDirectory));
        }

        return manifests;
    }

    private static ZigManifest ParseManifest(
        string path,
        string localCacheDirectory)
    {
        RejectLinkTraversal(
            localCacheDirectory,
            path,
            includeFinalEntry: true);
        var lines = ReadBoundedLines(
            path,
            MaximumManifestBytes,
            MaximumManifestRecords + 1,
            "Zig h manifest");
        if (lines.Count == 0 || lines[0] != "0")
        {
            throw new InvalidDataException(
                "A Zig h manifest has an unsupported header.");
        }

        var records = new List<ZigManifestRecord>(lines.Count - 1);
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
            {
                throw new InvalidDataException(
                    "A Zig h manifest contains an empty record.");
            }

            records.Add(ParseManifestRecord(lines[i]));
        }

        if (records.Count > MaximumManifestRecords)
        {
            throw new InvalidDataException(
                "A Zig h manifest has an invalid record count.");
        }

        return new ZigManifest(records);
    }

    private static ZigManifestRecord ParseManifestRecord(string line)
    {
        var fields = new string[5];
        var offset = 0;
        for (var field = 0; field < fields.Length; field++)
        {
            var separator = line.IndexOf(' ', offset);
            if (separator <= offset)
            {
                throw new InvalidDataException(
                    "A Zig h manifest contains a malformed record.");
            }

            fields[field] = line[offset..separator];
            offset = separator + 1;
        }

        var path = line[offset..];
        if (path.Length == 0
            || path.Length > MaximumManifestPathCharacters
            || path[0] == ' '
            || path[^1] == ' '
            || path.Any(character => char.IsControl(character))
            || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A Zig h manifest contains an invalid path.");
        }

        if (!long.TryParse(
                fields[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var length)
            || length < 0
            || length > MaximumEvidenceFileBytes
            || !ulong.TryParse(
                fields[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)
            || !long.TryParse(
                fields[2],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _)
            || fields[3].Length != 32
            || fields[3].Any(character => !IsLowerHex(character))
            || !int.TryParse(
                fields[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var prefix)
            || prefix is < 0 or > 3)
        {
            throw new InvalidDataException(
                "A Zig h manifest contains invalid record metadata.");
        }

        ValidateManifestPath(path, prefix);
        return new ZigManifestRecord(length, fields[3], prefix, path);
    }

    private static ZigManifest SelectCompileManifest(
        IReadOnlyList<ZigManifest> manifests,
        IReadOnlyList<string> archives,
        EvidencePaths paths)
    {
        var rootModule = Path.Combine(
            paths.GhosttySourceDirectory,
            "src",
            "main_c.zig");
        var candidates = manifests.Where(manifest =>
            ContainsPath(manifest, rootModule, requiredPrefix: 0, paths)
            && ContainsPath(
                manifest,
                paths.MetallibPath,
                requiredPrefix: 0,
                paths)
            && archives.All(archive =>
                ContainsPath(
                    manifest,
                    archive,
                    requiredPrefix: 2,
                    paths))).ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidDataException(
                "The fresh Zig cache must contain one unambiguous ghostty compile manifest.");
        }

        return candidates[0];
    }

    private static ZigManifest SelectBuildGraphManifest(
        IReadOnlyList<ZigManifest> manifests,
        EvidencePaths paths)
    {
        string[] requiredGhosttyFiles =
        [
            "build.zig",
            "build.zig.zon",
            "src/build/GhosttyLib.zig",
            "src/build/SharedDeps.zig",
        ];
        var buildRunner = Path.Combine(
            paths.ZigLibraryDirectory,
            "compiler",
            "build_runner.zig");
        var candidates = manifests.Where(manifest =>
            requiredGhosttyFiles.All(relativePath =>
                ContainsPath(
                    manifest,
                    Path.Combine(
                        paths.GhosttySourceDirectory,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)),
                    requiredPrefix: 0,
                    paths))
            && ContainsPath(
                manifest,
                buildRunner,
                requiredPrefix: 1,
                paths)).ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidDataException(
                "The fresh Zig cache must contain one unambiguous build-graph manifest.");
        }

        return candidates[0];
    }

    private static bool ContainsPath(
        ZigManifest manifest,
        string expectedPath,
        int requiredPrefix,
        EvidencePaths paths) =>
        manifest.Records.Any(record =>
            record.Prefix == requiredPrefix
            && PathsEqual(ResolveManifestPath(record, paths), expectedPath));

    private static ObservedClosure ObserveClosure(
        ZigManifest manifest,
        EvidencePaths paths,
        IReadOnlyCollection<string> archiveArguments)
    {
        var primary = new List<PendingPrimaryFile>();
        var global = new List<PendingGlobalFile>();
        var derived = new List<ObservedDerivedFile>();
        var expectedArchives = archiveArguments.ToHashSet(PathComparer);
        var observedArchives = new HashSet<string>(PathComparer);
        var derivedRecordCount = 0;
        long derivedBytes = 0;

        foreach (var record in manifest.Records)
        {
            var lexicalPath = ResolveManifestPath(record, paths);
            if (record.Prefix == 2)
            {
                var file = ObserveRegularFile(
                    lexicalPath,
                    paths.ZigLocalCacheDirectory,
                    allowSdkLinks: false);
                RequireManifestContent(record, file);
                if (expectedArchives.Contains(lexicalPath))
                {
                    if (!observedArchives.Add(lexicalPath))
                    {
                        throw new InvalidDataException(
                            "The compile manifest repeats a selected archive.");
                    }

                    // Static archives can contain absolute build-root strings in
                    // debug-only Mach-O sections even when they link to the same
                    // final dylib. Their current bytes are still checked against
                    // Zig's manifest above. Canonical provenance records the
                    // ordered archive arguments plus the shipped dylib instead
                    // of claiming reproducible intermediate archive bytes.
                    continue;
                }

                derived.Add(new ObservedDerivedFile(
                    Path.GetFileName(record.Path),
                    file.Length,
                    file.Sha256));
                derivedRecordCount++;
                derivedBytes = checked(derivedBytes + file.Length);
                continue;
            }

            if (record.Prefix == 3)
            {
                var file = ObserveRegularFile(
                    lexicalPath,
                    paths.ZigGlobalCacheDirectory,
                    allowSdkLinks: false);
                RequireManifestContent(record, file);
                var (packageKey, relativePath) =
                    ParseGlobalCacheLogicalParts(record.Path);
                global.Add(new PendingGlobalFile(
                    packageKey,
                    relativePath,
                    file.Length,
                    file.Sha256));
                continue;
            }

            var mapped = MapPrimaryPath(record, lexicalPath, paths);
            var observed = ObserveRegularFile(
                mapped.PhysicalPath,
                mapped.TrustRoot,
                mapped.AllowSdkLinks);
            RequireManifestContent(record, observed);
            primary.Add(new PendingPrimaryFile(
                mapped.LogicalPath,
                observed.Length,
                observed.Sha256));
        }

        if (!observedArchives.SetEquals(expectedArchives))
        {
            throw new InvalidDataException(
                "The compile manifest omits a selected archive.");
        }

        primary.AddRange(NormalizeGlobalCacheFiles(global));
        var files = MergePrimaryFiles(primary);
        long primaryBytes = 0;
        foreach (var file in files)
        {
            primaryBytes = checked(primaryBytes + file.Length);
        }

        return new ObservedClosure(
            files,
            derived
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ThenBy(file => file.Length)
                .ThenBy(file => file.Sha256, StringComparer.Ordinal)
                .ToArray(),
            files.Count,
            primaryBytes,
            ComputeFileListDigest(files),
            derivedRecordCount,
            derivedBytes);
    }

    private static PrimaryPath MapPrimaryPath(
        ZigManifestRecord record,
        string lexicalPath,
        EvidencePaths paths)
    {
        if (record.Prefix == 1)
        {
            return new PrimaryPath(
                ToLogicalPath("zig-lib", paths.ZigLibraryDirectory, lexicalPath),
                lexicalPath,
                paths.ZigLibraryDirectory,
                AllowSdkLinks: false);
        }

        if (record.Prefix != 0)
        {
            throw new InvalidDataException(
                "A primary Zig h-manifest record has an unsupported prefix.");
        }

        if (PathsEqual(lexicalPath, paths.MetallibPath))
        {
            return new PrimaryPath(
                "derived/ghostty-metallib",
                lexicalPath,
                paths.RepositoryRoot,
                AllowSdkLinks: false);
        }

        if (IsWithin(lexicalPath, paths.GhosttySourceDirectory))
        {
            return new PrimaryPath(
                ToLogicalPath(
                    "ghostty",
                    paths.GhosttySourceDirectory,
                    lexicalPath),
                lexicalPath,
                paths.GhosttySourceDirectory,
                AllowSdkLinks: false);
        }

        if (IsWithin(lexicalPath, paths.SdkDirectory))
        {
            var resolved = ResolveSdkPath(lexicalPath, paths.SdkDirectory);
            return new PrimaryPath(
                ToLogicalPath("sdk", paths.SdkDirectory, lexicalPath),
                resolved,
                paths.SdkDirectory,
                AllowSdkLinks: true);
        }

        if (IsWithin(lexicalPath, paths.RepositoryRoot))
        {
            return new PrimaryPath(
                ToLogicalPath(
                    "repository",
                    paths.RepositoryRoot,
                    lexicalPath),
                lexicalPath,
                paths.RepositoryRoot,
                AllowSdkLinks: false);
        }

        throw new InvalidDataException(
            "A Zig h-manifest primary input escapes the pinned logical roots.");
    }

    private static IReadOnlyList<PendingPrimaryFile> NormalizeGlobalCacheFiles(
        IReadOnlyList<PendingGlobalFile> globalFiles)
    {
        var normalized = new List<PendingPrimaryFile>();
        foreach (var package in globalFiles.GroupBy(
                     file => file.PackageKey,
                     StringComparer.Ordinal))
        {
            var packageFiles = MergePackageFiles(package);
            var packageDigest = ComputeFileListDigest(packageFiles);
            foreach (var file in packageFiles)
            {
                normalized.Add(new PendingPrimaryFile(
                    $"zig-global-cache/package-{packageDigest}/{file.Path}",
                    file.Length,
                    file.Sha256));
            }
        }

        return normalized;
    }

    private static IReadOnlyList<ObservedPrimaryFile> MergePackageFiles(
        IEnumerable<PendingGlobalFile> files)
    {
        var merged = new Dictionary<string, ObservedPrimaryFile>(
            StringComparer.Ordinal);
        foreach (var file in files)
        {
            var observed = new ObservedPrimaryFile(
                file.RelativePath,
                file.Length,
                file.Sha256);
            MergeFile(merged, observed);
        }

        return merged.Values
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ObservedPrimaryFile> MergePrimaryFiles(
        IEnumerable<PendingPrimaryFile> files)
    {
        var merged = new Dictionary<string, ObservedPrimaryFile>(
            StringComparer.Ordinal);
        foreach (var file in files)
        {
            ValidateLogicalPath(file.LogicalPath);
            MergeFile(
                merged,
                new ObservedPrimaryFile(
                    file.LogicalPath,
                    file.Length,
                    file.Sha256));
        }

        return merged.Values
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static void MergeFile(
        IDictionary<string, ObservedPrimaryFile> files,
        ObservedPrimaryFile candidate)
    {
        if (!files.TryGetValue(candidate.Path, out var existing))
        {
            files.Add(candidate.Path, candidate);
            return;
        }

        if (existing.Length != candidate.Length
            || existing.Sha256 != candidate.Sha256)
        {
            throw new InvalidDataException(
                "A logical build-evidence path has conflicting content.");
        }
    }

    private static IReadOnlyList<string> NormalizeSemanticCommand(
        IReadOnlyList<string> tokens,
        EvidencePaths paths,
        IReadOnlySet<string> archivePaths)
    {
        var normalized = new List<string>(tokens.Count);
        var directoryBudget = new SemanticDirectoryBudget();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (TryGetSemanticPathOptionKind(token, out var optionKind))
            {
                normalized.Add(token);
                if (++index >= tokens.Count)
                {
                    throw new InvalidDataException(
                        "The final compiler command omits a path-valued option.");
                }

                normalized.Add(NormalizeSemanticPath(
                    tokens[index],
                    paths,
                    optionKind,
                    archivePaths,
                    directoryBudget));
                continue;
            }

            if (IsSemanticLiteralOption(token))
            {
                normalized.Add(token);
                if (++index >= tokens.Count)
                {
                    throw new InvalidDataException(
                        "The final compiler command omits an option value.");
                }

                var value = tokens[index];
                if (token == "-install_name" && value != InstallName)
                {
                    throw new InvalidDataException(
                        "The final compiler command has an unexpected install name.");
                }

                normalized.Add(value);
                continue;
            }

            if (token.StartsWith("-M", StringComparison.Ordinal))
            {
                var equals = token.IndexOf('=', 2);
                if (equals <= 2 || equals == token.Length - 1)
                {
                    throw new InvalidDataException(
                        "The final compiler command has a malformed module token.");
                }

                normalized.Add(
                    token[..(equals + 1)]
                    + NormalizeSemanticPath(
                        token[(equals + 1)..],
                        paths,
                        SemanticPathKind.File,
                        archivePaths,
                        directoryBudget));
                continue;
            }

            if (TryNormalizeAttachedSemanticPath(
                    token,
                    paths,
                    archivePaths,
                    directoryBudget,
                    out var attached))
            {
                normalized.Add(attached);
                continue;
            }

            if (TryResolveSemanticFile(token, paths, out var resolved))
            {
                normalized.Add(NormalizeSemanticPath(
                    resolved,
                    paths,
                    SemanticPathKind.File,
                    archivePaths,
                    directoryBudget));
                continue;
            }

            RejectUnboundPathSyntax(token, paths);
            normalized.Add(token);
        }

        if (normalized.Count != tokens.Count)
        {
            throw new InvalidDataException(
                "The normalized final compiler command lost token multiplicity.");
        }

        return normalized;
    }

    private static bool TryGetSemanticPathOptionKind(
        string token,
        out SemanticPathKind kind)
    {
        if (token is "-I"
            or "-L"
            or "-iframework"
            or "-isystem"
            or "--cache-dir"
            or "--global-cache-dir"
            or "--zig-lib-dir")
        {
            kind = SemanticPathKind.Directory;
            return true;
        }

        if (token == "--libc")
        {
            kind = SemanticPathKind.File;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsSemanticLiteralOption(string token) =>
        token is "--dep"
            or "--name"
            or "-framework"
            or "-install_name"
            or "-mcpu"
            or "-target";

    private static bool TryNormalizeAttachedSemanticPath(
        string token,
        EvidencePaths paths,
        IReadOnlySet<string> archivePaths,
        SemanticDirectoryBudget directoryBudget,
        out string normalized)
    {
        string[] equalsPrefixes =
        [
            "--cache-dir=",
            "--global-cache-dir=",
            "--libc=",
            "--zig-lib-dir=",
        ];
        foreach (var prefix in equalsPrefixes)
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal)
                && token.Length > prefix.Length)
            {
                var kind = prefix == "--libc="
                    ? SemanticPathKind.File
                    : SemanticPathKind.Directory;
                normalized = prefix
                    + NormalizeSemanticPath(
                        token[prefix.Length..],
                        paths,
                        kind,
                        archivePaths,
                        directoryBudget);
                return true;
            }
        }

        string[] attachedPrefixes =
        [
            "-iframework",
            "-isystem",
            "-I",
            "-L",
        ];
        foreach (var prefix in attachedPrefixes)
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal)
                && token.Length > prefix.Length)
            {
                normalized = prefix
                    + NormalizeSemanticPath(
                        token[prefix.Length..],
                        paths,
                        SemanticPathKind.Directory,
                        archivePaths,
                        directoryBudget);
                return true;
            }
        }

        normalized = string.Empty;
        return false;
    }

    private static bool TryResolveSemanticFile(
        string token,
        EvidencePaths paths,
        out string path)
    {
        if (token.Length == 0
            || token[0] == '-'
            || token == InstallName)
        {
            path = string.Empty;
            return false;
        }

        if (!TryResolveArgumentPath(
                token,
                paths.RepositoryRoot,
                out var candidate)
            || !File.Exists(candidate))
        {
            path = string.Empty;
            return false;
        }

        path = candidate;
        return true;
    }

    private static string NormalizeSemanticPath(
        string value,
        EvidencePaths paths,
        SemanticPathKind expectedKind,
        IReadOnlySet<string> archivePaths,
        SemanticDirectoryBudget directoryBudget)
    {
        var path = ResolveArgumentPath(value, paths.RepositoryRoot);
        if (PathsEqual(path, paths.ZigExecutablePath))
        {
            RequireSemanticPathKind(
                path,
                expectedKind,
                SemanticPathKind.File);
            var file = ObserveRegularFile(
                path,
                paths.RepositoryRoot,
                allowSdkLinks: false);
            return SemanticFile("zig-tool", "zig", file);
        }

        if (PathsEqual(path, paths.ZigLocalCacheDirectory))
        {
            RequireSemanticPathKind(
                path,
                expectedKind,
                SemanticPathKind.Directory);
            _ = RejectLinkTraversal(
                paths.ZigLocalCacheDirectory,
                path,
                includeFinalEntry: true);
            return "zig-local-cache:directory-root";
        }

        if (PathsEqual(path, paths.ZigGlobalCacheDirectory))
        {
            RequireSemanticPathKind(
                path,
                expectedKind,
                SemanticPathKind.Directory);
            _ = RejectLinkTraversal(
                paths.ZigGlobalCacheDirectory,
                path,
                includeFinalEntry: true);
            return "zig-global-cache:directory-root";
        }

        if (PathsEqual(path, paths.ZigLibraryDirectory))
        {
            RequireSemanticPathKind(
                path,
                expectedKind,
                SemanticPathKind.Directory);
            _ = RejectLinkTraversal(
                paths.ZigLibraryDirectory,
                path,
                includeFinalEntry: true);
            return "zig-lib:directory-root:content-bound-by=compileClosure";
        }

        if (IsWithin(path, paths.ZigLocalCacheDirectory))
        {
            return NormalizeSemanticCachePath(
                path,
                paths.ZigLocalCacheDirectory,
                "zig-local-cache",
                packageCache: false,
                expectedKind,
                archivePaths,
                directoryBudget);
        }

        if (IsWithin(path, paths.ZigGlobalCacheDirectory))
        {
            return NormalizeSemanticCachePath(
                path,
                paths.ZigGlobalCacheDirectory,
                "zig-global-cache",
                packageCache: true,
                expectedKind,
                archivePaths,
                directoryBudget);
        }

        if (IsWithin(path, paths.GhosttySourceDirectory))
        {
            return NormalizeSemanticPrimaryPath(
                path,
                paths.GhosttySourceDirectory,
                "ghostty",
                allowSdkLinks: false,
                expectedKind);
        }

        if (IsWithin(path, paths.ZigLibraryDirectory))
        {
            return NormalizeSemanticPrimaryPath(
                path,
                paths.ZigLibraryDirectory,
                "zig-lib",
                allowSdkLinks: false,
                expectedKind);
        }

        if (IsWithin(path, paths.SdkDirectory))
        {
            return NormalizeSemanticPrimaryPath(
                path,
                paths.SdkDirectory,
                "sdk",
                allowSdkLinks: true,
                expectedKind);
        }

        if (IsWithin(path, paths.InstallDirectory))
        {
            return NormalizeSemanticPrimaryPath(
                path,
                paths.InstallDirectory,
                "ghostty-install",
                allowSdkLinks: false,
                expectedKind);
        }

        if (IsWithin(path, paths.RepositoryRoot))
        {
            return NormalizeSemanticPrimaryPath(
                path,
                paths.RepositoryRoot,
                "repository",
                allowSdkLinks: false,
                expectedKind);
        }

        throw new InvalidDataException(
            "The final compiler command contains a path outside its pinned roots.");
    }

    private static string NormalizeSemanticCachePath(
        string path,
        string cacheRoot,
        string prefix,
        bool packageCache,
        SemanticPathKind expectedKind,
        IReadOnlySet<string> archivePaths,
        SemanticDirectoryBudget directoryBudget)
    {
        var physical = RejectLinkTraversal(
            cacheRoot,
            path,
            includeFinalEntry: true);
        var relative = Path.GetRelativePath(cacheRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        var validRoot = packageCache
            ? segments[0] == "p"
                && segments.Length >= 2
                && IsSafeCacheKey(segments[1])
            : segments.Length >= 2
                && segments[0] is "c" or "o"
                && IsSafeCacheKey(segments[1]);
        if (!validRoot
            || segments.Skip(2).Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "The final compiler command contains an invalid cache path.");
        }

        var logical = segments.Length == 2
            ? "."
            : string.Join('/', segments[2..]);
        RequireSemanticPathKind(physical, expectedKind);
        if (expectedKind == SemanticPathKind.File)
        {
            var file = ObserveRegularFile(
                physical,
                cacheRoot,
                allowSdkLinks: false);
            if (!packageCache && archivePaths.Contains(physical))
            {
                return $"{prefix}:archive:{Path.GetFileName(physical)}";
            }

            return SemanticFile(prefix, logical, file);
        }

        var directoryDigest = ObserveSemanticDirectoryDigest(
            physical,
            cacheRoot,
            directoryBudget);
        return $"{prefix}:directory:{logical}:"
            + $"tree-sha256={directoryDigest}";
    }

    private static string NormalizeSemanticPrimaryPath(
        string path,
        string root,
        string prefix,
        bool allowSdkLinks,
        SemanticPathKind expectedKind)
    {
        var physical = allowSdkLinks
            ? ResolveSdkPath(path, root, expectedKind)
            : RejectLinkTraversal(root, path, includeFinalEntry: true);
        var logical = ToLogicalPath(prefix, root, path);
        RequireSemanticPathKind(physical, expectedKind);
        if (expectedKind == SemanticPathKind.File)
        {
            var file = ObserveRegularFile(
                physical,
                root,
                allowSdkLinks);
            return SemanticFile(prefix, logical[(prefix.Length + 1)..], file);
        }

        return $"{prefix}:directory:{logical[(prefix.Length + 1)..]}:"
            + "content-bound-by=compileClosure";
    }

    private static string SemanticFile(
        string prefix,
        string logical,
        ObservedFile file) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}:file:{logical}:{file.Length}:{file.Sha256}");

    private static void RequireSemanticPathKind(
        string path,
        SemanticPathKind expectedKind,
        SemanticPathKind? knownKind = null)
    {
        SemanticPathKind actualKind;
        if (knownKind is not null)
        {
            actualKind = knownKind.Value;
        }
        else
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or DirectoryNotFoundException)
            {
                throw new InvalidDataException(
                    "The final compiler command path does not exist.",
                    exception);
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "The final compiler command path is a symbolic link.");
            }

            actualKind = attributes.HasFlag(FileAttributes.Directory)
                ? SemanticPathKind.Directory
                : SemanticPathKind.File;
        }

        if (actualKind != expectedKind)
        {
            throw new InvalidDataException(
                "The final compiler command path has an unexpected entry type.");
        }
    }

    private static string ObserveSemanticDirectoryDigest(
        string directory,
        string trustRoot,
        SemanticDirectoryBudget budget)
    {
        var cacheKey = new SemanticDirectoryCacheKey(
            NormalizePath(trustRoot),
            NormalizePath(directory));
        if (budget.TryGetDigest(cacheKey, out var cachedDigest))
        {
            return cachedDigest;
        }

        var root = InspectNoLinkPath(
            trustRoot,
            directory,
            includeFinalEntry: true);
        if (root is not DirectoryInfo rootDirectory)
        {
            throw new InvalidDataException(
                "A semantic compiler directory is not a directory.");
        }

        var entries = new List<SemanticDirectoryEntry>();
        var pending = new Stack<(DirectoryInfo Directory, string Relative, int Depth)>();
        pending.Push((rootDirectory, string.Empty, 0));
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Depth > MaximumSemanticDirectoryDepth)
            {
                throw new InvalidDataException(
                    "A semantic compiler directory exceeds the depth limit.");
            }

            var children = current.Directory
                .EnumerateFileSystemInfos(
                    "*",
                    new EnumerationOptions
                    {
                        AttributesToSkip = 0,
                        IgnoreInaccessible = false,
                        RecurseSubdirectories = false,
                        ReturnSpecialDirectories = false,
                    })
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (var child in children)
            {
                InspectLink(child);
                if (entries.Count >= MaximumSemanticDirectoryEntries)
                {
                    throw new InvalidDataException(
                        "A semantic compiler directory exceeds the entry limit.");
                }

                budget.ReserveEntry();
                var relative = current.Relative.Length == 0
                    ? child.Name
                    : $"{current.Relative}/{child.Name}";
                ValidateSemanticDirectoryPath(relative);
                if (child is DirectoryInfo childDirectory)
                {
                    entries.Add(new SemanticDirectoryEntry(
                        "directory",
                        relative,
                        0,
                        string.Empty));
                    pending.Push((
                        childDirectory,
                        relative,
                        current.Depth + 1));
                    continue;
                }

                if (child is not FileInfo)
                {
                    throw new InvalidDataException(
                        "A semantic compiler directory contains an unsupported entry.");
                }

                var remainingBytes = Math.Min(
                    MaximumSemanticDirectoryBytes - totalBytes,
                    budget.RemainingBytes);
                var file = ObserveRegularFile(
                    child.FullName,
                    trustRoot,
                    allowSdkLinks: false,
                    maximumBytes: remainingBytes);
                totalBytes = checked(totalBytes + file.Length);
                budget.AddBytes(file.Length);

                entries.Add(new SemanticDirectoryEntry(
                    "file",
                    relative,
                    file.Length,
                    file.Sha256));
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries.OrderBy(
                         entry => entry.Path,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", entry.Kind);
                writer.WriteString("path", entry.Path);
                if (entry.Kind == "file")
                {
                    writer.WriteNumber("length", entry.Length);
                    writer.WriteString("sha256", entry.Sha256);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.Flush();
        }

        var digest = Convert.ToHexString(SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
        budget.StoreDigest(cacheKey, digest);
        return digest;
    }

    private static void ValidateSemanticDirectoryPath(string path)
    {
        if (path.Length == 0
            || path.Length > MaximumManifestPathCharacters
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Any(character => char.IsControl(character))
            || path.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "A semantic compiler directory contains an invalid path.");
        }
    }

    private static void RejectUnboundPathSyntax(
        string token,
        EvidencePaths paths)
    {
        var embedsPinnedRoot = new[]
        {
            paths.RepositoryRoot,
            paths.ZigLocalCacheDirectory,
            paths.ZigGlobalCacheDirectory,
            paths.SdkDirectory,
            paths.InstallDirectory,
        }.Any(root => token.Contains(root, PathComparison));
        if (embedsPinnedRoot
            || token.Contains('/', StringComparison.Ordinal)
            || token.Contains('\\', StringComparison.Ordinal)
            || token.Contains('@', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The final compiler command contains unbound path syntax.");
        }
    }

    // Canonical output

    private static byte[] WriteCanonicalJson(
        FinalCommandEvidence command,
        ObservedFile output,
        ObservedClosure compileClosure,
        ObservedClosure buildGraphClosure)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("generator", Generator);
            writer.WriteString("target", Target);
            writer.WriteString("optimization", Optimization);
            writer.WriteString("installName", InstallName);
            writer.WriteString("rootModule", "ghostty/src/main_c.zig");
            writer.WriteStartObject("finalArtifact");
            writer.WriteNumber("length", output.Length);
            writer.WriteString("sha256", output.Sha256);
            writer.WriteEndObject();
            WriteStringArray(writer, "archiveNames", command.ArchiveNames);
            WriteStringArray(writer, "frameworks", command.Frameworks);
            WriteStringArray(
                writer,
                "systemLibraries",
                command.SystemLibraries);
            WriteStringArray(
                writer,
                "semanticCommand",
                command.SemanticCommand);
            writer.WriteStartArray("derivedModules");
            foreach (var module in command.DerivedModules)
            {
                writer.WriteStartObject();
                writer.WriteString("name", module.Name);
                writer.WriteNumber("length", module.Length);
                writer.WriteString("sha256", module.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteClosure(writer, "compileClosure", compileClosure);
            WriteClosure(writer, "buildGraphClosure", buildGraphClosure);
            writer.WriteEndObject();
            writer.Flush();
        }

        if (stream.Length >= MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "The canonical native build evidence exceeds the byte limit.");
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteClosure(
        Utf8JsonWriter writer,
        string propertyName,
        ObservedClosure closure)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteStartArray("files");
        foreach (var file in closure.Files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteNumber("length", file.Length);
            writer.WriteString("sha256", file.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("derivedFiles");
        foreach (var file in closure.DerivedFiles)
        {
            writer.WriteStartObject();
            writer.WriteString("name", file.Name);
            writer.WriteNumber("length", file.Length);
            writer.WriteString("sha256", file.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("fileCount", closure.FileCount);
        writer.WriteNumber("totalBytes", closure.TotalBytes);
        writer.WriteString("digestSha256", closure.DigestSha256);
        writer.WriteNumber(
            "derivedRecordCount",
            closure.DerivedRecordCount);
        writer.WriteNumber("derivedBytes", closure.DerivedBytes);
        writer.WriteEndObject();
    }

    private static string ComputeFileListDigest(
        IReadOnlyList<ObservedPrimaryFile> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var file in files.OrderBy(
                         file => file.Path,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("length", file.Length);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
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

    // Filesystem, token, and path primitives

    private static IReadOnlyList<string> ReadBoundedLines(
        string path,
        int maximumBytes,
        int maximumLines,
        string label)
    {
        using var stream = RegularPackageFileReader.Open(path, out var inspection);
        if (inspection.Length < 0 || inspection.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {label} exceeds its byte limit.");
        }

        var content = new byte[checked((int)inspection.Length)];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = stream.Read(content, offset, content.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"The {label} became shorter while reading.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"The {label} became longer while reading.");
        }

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i <= content.Length; i++)
        {
            if (i < content.Length && content[i] != (byte)'\n')
            {
                continue;
            }

            var length = i - start;
            if (length > 0 && content[i - 1] == (byte)'\r')
            {
                length--;
            }

            if (length > MaximumLineBytes)
            {
                throw new InvalidDataException(
                    $"The {label} contains an oversized line.");
            }

            if (i == content.Length && start == content.Length)
            {
                break;
            }

            lines.Add(StrictUtf8.GetString(content, start, length));
            if (lines.Count > maximumLines)
            {
                throw new InvalidDataException(
                    $"The {label} exceeds its line limit.");
            }

            start = i + 1;
        }

        return lines;
    }

    private static IReadOnlyList<string> Tokenize(string line, int lineNumber)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var tokenStarted = false;
        var quote = '\0';
        var escaping = false;

        foreach (var character in line)
        {
            if (character == '\0'
                || (char.IsControl(character) && character != '\t'))
            {
                throw TraceTokenError(lineNumber);
            }

            if (escaping)
            {
                token.Append(character);
                tokenStarted = true;
                escaping = false;
                continue;
            }

            if (quote == '\'')
            {
                if (character == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(character);
                }

                tokenStarted = true;
                continue;
            }

            if (character == '\\')
            {
                escaping = true;
                tokenStarted = true;
                continue;
            }

            if (quote == '"')
            {
                if (character == '"')
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(character);
                }

                tokenStarted = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                tokenStarted = true;
                continue;
            }

            if (character is ' ' or '\t')
            {
                if (tokenStarted)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                    if (tokens.Count > MaximumTokensPerLine)
                    {
                        throw TraceTokenError(lineNumber);
                    }
                }

                continue;
            }

            token.Append(character);
            tokenStarted = true;
        }

        if (escaping || quote != '\0')
        {
            throw TraceTokenError(lineNumber);
        }

        if (tokenStarted)
        {
            tokens.Add(token.ToString());
        }

        if (tokens.Count > MaximumTokensPerLine)
        {
            throw TraceTokenError(lineNumber);
        }

        return tokens;
    }

    private static InvalidDataException TraceTokenError(int lineNumber) =>
        new($"The native build trace has malformed quoting on line {lineNumber}.");

    private static ObservedFile ObserveRegularFile(
        string path,
        string trustRoot,
        bool allowSdkLinks,
        long maximumBytes = MaximumEvidenceFileBytes)
    {
        if (maximumBytes < 0 || maximumBytes > MaximumEvidenceFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var physicalPath = allowSdkLinks
            ? path
            : RejectLinkTraversal(
                trustRoot,
                path,
                includeFinalEntry: true);
        using var stream = RegularPackageFileReader.Open(
            physicalPath,
            out var inspection);
        if (inspection.Length < 0
            || inspection.Length > maximumBytes)
        {
            throw new InvalidDataException(
                "A native build-evidence file exceeds the byte limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var zigDigest = new ZigManifestContentDigest();
        var buffer = new byte[128 * 1024];
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
                    "A native build-evidence file became shorter while hashing.");
            }

            hash.AppendData(buffer, 0, read);
            zigDigest.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "A native build-evidence file became longer while hashing.");
        }

        return new ObservedFile(
            inspection.Length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            zigDigest.FinishHex());
    }

    private static string RejectLinkTraversal(
        string root,
        string path,
        bool includeFinalEntry)
    {
        _ = InspectNoLinkPath(root, path, includeFinalEntry);
        return NormalizePath(path);
    }

    private static FileSystemInfo InspectNoLinkPath(
        string root,
        string path,
        bool includeFinalEntry)
    {
        var normalizedRoot = NormalizePath(root);
        var normalizedPath = NormalizePath(path);
        if (!IsWithin(normalizedPath, normalizedRoot))
        {
            throw new InvalidDataException(
                "A native build-evidence path escapes its trust root.");
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        var rootEntry = new DirectoryInfo(normalizedRoot);
        InspectLink(rootEntry);
        if (relative == ".")
        {
            return rootEntry;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar);
        var current = rootEntry;
        FileSystemInfo lastEntry = rootEntry;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!includeFinalEntry && i == segments.Length - 1)
            {
                break;
            }

            var matching = current.EnumerateFileSystemInfos(
                    "*",
                    new EnumerationOptions
                    {
                        AttributesToSkip = 0,
                        IgnoreInaccessible = false,
                        RecurseSubdirectories = false,
                        ReturnSpecialDirectories = false,
                    })
                .Where(entry => string.Equals(
                    entry.Name,
                    segments[i],
                    PathComparison))
                .Take(2)
                .ToArray();
            if (matching.Length != 1)
            {
                throw new InvalidDataException(
                    "A native build-evidence path does not exist unambiguously.");
            }

            var entry = matching[0];
            InspectLink(entry);
            lastEntry = entry;
            if (i != segments.Length - 1)
            {
                current = entry as DirectoryInfo
                    ?? throw new InvalidDataException(
                        "A native build-evidence path traverses a non-directory.");
            }
        }

        return lastEntry;
    }

    private static void InspectLink(FileSystemInfo entry)
    {
        entry.Refresh();
        if (!entry.Exists)
        {
            throw new InvalidDataException(
                "A native build-evidence path does not exist.");
        }

        if (entry.LinkTarget is not null
            || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "A native build-evidence path contains a symbolic link.");
        }
    }

    private static string ResolveSdkPath(
        string path,
        string sdkRoot,
        SemanticPathKind expectedKind = SemanticPathKind.File)
    {
        var normalizedRoot = NormalizePath(sdkRoot);
        var normalizedPath = NormalizePath(path);
        if (!IsWithin(normalizedPath, normalizedRoot))
        {
            throw new InvalidDataException(
                "An SDK evidence path escapes the pinned SDK.");
        }

        InspectLink(new DirectoryInfo(normalizedRoot));
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        if (relative == ".")
        {
            RequireSemanticPathKind(
                normalizedRoot,
                expectedKind,
                SemanticPathKind.Directory);
            return normalizedRoot;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar);
        var current = normalizedRoot;
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(current, segments[i]);
            var currentDirectory = new DirectoryInfo(current);
            var matching = currentDirectory.EnumerateFileSystemInfos(
                    "*",
                    new EnumerationOptions
                    {
                        AttributesToSkip = 0,
                        IgnoreInaccessible = false,
                        RecurseSubdirectories = false,
                        ReturnSpecialDirectories = false,
                    })
                .Where(entry => string.Equals(
                    entry.Name,
                    segments[i],
                    PathComparison))
                .Take(2)
                .ToArray();
            if (matching.Length != 1)
            {
                throw new InvalidDataException(
                    "An SDK evidence path does not exist unambiguously.");
            }

            var entry = matching[0];
            entry.Refresh();
            if (!entry.Exists)
            {
                throw new InvalidDataException(
                    "An SDK evidence path does not exist.");
            }

            if (entry.LinkTarget is not null
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var target = entry.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new InvalidDataException(
                        "An SDK symbolic link cannot be resolved.");
                current = NormalizePath(target.FullName);
                if (!IsWithin(current, normalizedRoot))
                {
                    throw new InvalidDataException(
                        "An SDK symbolic link escapes the pinned SDK.");
                }
            }
            else
            {
                current = candidate;
            }

            if (i != segments.Length - 1)
            {
                RequireSemanticPathKind(
                    current,
                    SemanticPathKind.Directory);
            }
        }

        RequireSemanticPathKind(current, expectedKind);
        return current;
    }

    private static string ResolveManifestPath(
        ZigManifestRecord record,
        EvidencePaths paths)
    {
        var root = record.Prefix switch
        {
            0 => paths.RepositoryRoot,
            1 => paths.ZigLibraryDirectory,
            2 => paths.ZigLocalCacheDirectory,
            3 => paths.ZigGlobalCacheDirectory,
            _ => throw new InvalidDataException(
                "A Zig h manifest has an unsupported prefix."),
        };
        return Path.IsPathRooted(record.Path)
            ? NormalizePath(record.Path)
            : NormalizePath(Path.Combine(
                root,
                record.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void ValidateManifestPath(string path, int prefix)
    {
        if (prefix != 0 && Path.IsPathRooted(path))
        {
            throw new InvalidDataException(
                "A prefixed Zig h-manifest path must be relative.");
        }

        var candidate = Path.IsPathRooted(path)
            ? path[1..]
            : path;
        var segments = candidate.Split('/');
        if (segments.Length == 0
            || segments.Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "A Zig h manifest contains a non-canonical path.");
        }
    }

    private static (string PackageKey, string RelativePath)
        ParseGlobalCacheLogicalParts(string path)
    {
        var segments = path.Split('/');
        if (segments.Length < 3
            || segments[0] != "p"
            || !IsSafeCacheKey(segments[1]))
        {
            throw new InvalidDataException(
                "A Zig global-cache record has an unexpected path shape.");
        }

        return (segments[1], string.Join('/', segments[2..]));
    }

    private static void ValidateLocalCacheOutputPath(
        string path,
        EvidencePaths paths,
        string expectedName)
    {
        var relative = Path.GetRelativePath(
            paths.ZigLocalCacheDirectory,
            path);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        if (segments.Length != 3
            || segments[0] != "o"
            || !IsSafeCacheKey(segments[1])
            || segments[2] != expectedName)
        {
            throw new InvalidDataException(
                "A Zig local-cache output has an unexpected path shape.");
        }
    }

    private static void ValidateLocalCacheDerivedPath(
        string path,
        EvidencePaths paths)
    {
        var relative = Path.GetRelativePath(
            paths.ZigLocalCacheDirectory,
            path);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        if (segments.Length < 3
            || segments[0] is not ("c" or "o")
            || !IsSafeCacheKey(segments[1])
            || segments.Skip(2).Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "A generated Zig module has an unexpected local-cache path.");
        }
    }

    private static string ToLogicalPath(
        string prefix,
        string root,
        string path)
    {
        var relative = Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relative == "."
            || relative.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(
                "A native build-evidence path cannot be normalized.");
        }

        return $"{prefix}/{relative}";
    }

    private static void ValidateLogicalPath(string path)
    {
        if (path.Length == 0
            || path.Length > MaximumManifestPathCharacters
            || Path.IsPathRooted(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Any(character => char.IsControl(character))
            || path.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "A canonical native build-evidence path is invalid.");
        }
    }

    private static bool IsGhosttyBuildLib(IReadOnlyList<string> tokens)
    {
        if (!tokens.Contains("build-lib", StringComparer.Ordinal))
        {
            return false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "--name"
                && i + 1 < tokens.Count
                && tokens[i + 1] == "ghostty")
            {
                return true;
            }

            if (tokens[i] == "--name=ghostty")
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireSingleOptionValue(
        IReadOnlyList<string> tokens,
        string option,
        string expected)
    {
        var values = ReadPairedValues(tokens, option);
        if (values.Count != 1 || values[0] != expected)
        {
            throw new InvalidDataException(
                $"The ghostty build-lib command has an unexpected {option} value.");
        }
    }

    private static void RequireAllOptionValues(
        IReadOnlyList<string> tokens,
        string option,
        string expected)
    {
        var values = ReadPairedValues(tokens, option);
        if (values.Count == 0
            || values.Any(value => value != expected))
        {
            throw new InvalidDataException(
                $"The ghostty build-lib command has an unexpected {option} value.");
        }
    }

    private static void RequireSinglePathOption(
        IReadOnlyList<string> tokens,
        string option,
        string expected,
        EvidencePaths paths)
    {
        var values = ReadPairedValues(tokens, option);
        if (values.Count != 1
            || !PathsEqual(
                ResolveArgumentPath(values[0], paths.RepositoryRoot),
                expected))
        {
            throw new InvalidDataException(
                $"The ghostty build-lib command has an unexpected {option} path.");
        }
    }

    private static void RequireAllPathOptions(
        IReadOnlyList<string> tokens,
        string option,
        string expected,
        EvidencePaths paths)
    {
        var values = ReadPairedValues(tokens, option);
        if (values.Count == 0
            || values.Any(value => !PathsEqual(
                ResolveArgumentPath(value, paths.RepositoryRoot),
                expected)))
        {
            throw new InvalidDataException(
                $"The ghostty build-lib command has an unexpected {option} path.");
        }
    }

    private static IReadOnlyList<string> ReadPairedValues(
        IReadOnlyList<string> tokens,
        string option)
    {
        var values = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == option)
            {
                if (i + 1 == tokens.Count)
                {
                    throw new InvalidDataException(
                        $"The ghostty build-lib command omits the {option} value.");
                }

                values.Add(tokens[++i]);
                continue;
            }

            if (tokens[i].StartsWith(
                    option + "=",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The ghostty build-lib command uses an unsupported {option} form.");
            }
        }

        return values;
    }

    private static void RequireFlagCount(
        IReadOnlyList<string> tokens,
        string flag,
        int expectedCount)
    {
        if (tokens.Count(token => token == flag) != expectedCount)
        {
            throw new InvalidDataException(
                $"The ghostty build-lib command has an unexpected {flag} count.");
        }
    }

    private static void RequireRootOptimization(
        IReadOnlyList<string> tokens)
    {
        var rootIndex = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].StartsWith("-Mroot=", StringComparison.Ordinal))
            {
                if (rootIndex != -1)
                {
                    throw new InvalidDataException(
                        "The ghostty build-lib command repeats the root module.");
                }

                rootIndex = i;
            }
        }

        var optimizationIndex = -1;
        for (var i = 0; i < rootIndex; i++)
        {
            if (tokens[i].StartsWith("-O", StringComparison.Ordinal))
            {
                optimizationIndex = i;
            }
        }

        var hasUnsupportedOptimization = tokens.Any(token =>
            token.StartsWith("-O", StringComparison.Ordinal)
            && token is not "-OReleaseFast" and not "-ODebug");
        if (rootIndex == -1
            || optimizationIndex == -1
            || tokens[optimizationIndex] != "-O" + Optimization
            || hasUnsupportedOptimization)
        {
            throw new InvalidDataException(
                "The ghostty build-lib command has an unexpected optimization.");
        }
    }

    private static IReadOnlyList<string> RequireUniqueSorted(
        IEnumerable<string> values,
        string label)
    {
        var ordered = values
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new InvalidDataException(
                $"The ghostty build-lib command repeats a {label}.");
        }

        return ordered;
    }

    private static bool IsSystemLibraryFlag(string token) =>
        token.Length > 2
        && token[0] == '-'
        && token[1] == 'l'
        && token[2] != '-';

    private static bool IsSafeSemanticName(string value) =>
        value.Length is > 0 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.' or '+');

    private static bool IsSafeModuleName(string value) =>
        value.Length is > 0 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.' or '+' or '#');

    private static bool IsSafeCacheKey(string value) =>
        value.Length is > 0 and <= 160
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.');

    private static bool IsLowerHexFileName(string name) =>
        name.Length == 36
        && name.EndsWith(".txt", StringComparison.Ordinal)
        && name.AsSpan(0, 32).IndexOfAnyExcept(
            "0123456789abcdef".AsSpan()) == -1;

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9'
        || character is >= 'a' and <= 'f';

    private static void RequireManifestContent(
        ZigManifestRecord record,
        ObservedFile file)
    {
        if (record.Length != file.Length
            || record.ContentDigest != file.ZigContentDigest)
        {
            throw new InvalidDataException(
                "A Zig h-manifest file no longer matches the observed content.");
        }
    }

    private static string ResolveArgumentPath(
        string value,
        string repositoryRoot) =>
        Path.IsPathRooted(value)
            ? NormalizePath(value)
            : NormalizePath(Path.GetFullPath(value, repositoryRoot));

    private static bool TryResolveArgumentPath(
        string value,
        string repositoryRoot,
        out string path)
    {
        try
        {
            path = ResolveArgumentPath(value, repositoryRoot);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            path = string.Empty;
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static bool IsWithin(string path, string root)
    {
        if (PathsEqual(path, root))
        {
            return true;
        }

        return path.StartsWith(
            Path.TrimEndingDirectorySeparator(root)
                + Path.DirectorySeparatorChar,
            PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static void RequireDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidDataException($"{label} does not exist.");
        }
    }

    private sealed record TraceLine(
        int Number,
        IReadOnlyList<string> Tokens);

    private sealed record ModuleArgument(string Name, string Path);

    private sealed record ArchiveArgument(string Name, string Path);

    private sealed record ObservedModule(
        string Name,
        long Length,
        string Sha256);

    private enum SemanticPathKind
    {
        File,
        Directory,
    }

    private sealed record SemanticDirectoryEntry(
        string Kind,
        string Path,
        long Length,
        string Sha256);

    private readonly record struct SemanticDirectoryCacheKey(
        string TrustRoot,
        string Directory);

    private sealed class SemanticDirectoryBudget
    {
        private readonly Dictionary<SemanticDirectoryCacheKey, string>
            _digests = [];
        private int _entryCount;
        private long _bytes;

        public long RemainingBytes =>
            MaximumSemanticDirectoryTotalBytes - _bytes;

        public bool TryGetDigest(
            SemanticDirectoryCacheKey key,
            out string digest) =>
            _digests.TryGetValue(key, out digest!);

        public void ReserveEntry()
        {
            if (_entryCount >= MaximumSemanticDirectoryTotalEntries)
            {
                throw new InvalidDataException(
                    "Semantic compiler directories exceed the cumulative entry limit.");
            }

            _entryCount++;
        }

        public void AddBytes(long bytes)
        {
            if (bytes < 0 || bytes > RemainingBytes)
            {
                throw new InvalidDataException(
                    "Semantic compiler directories exceed the cumulative byte limit.");
            }

            _bytes += bytes;
        }

        public void StoreDigest(
            SemanticDirectoryCacheKey key,
            string digest)
        {
            if (!_digests.TryAdd(key, digest))
            {
                throw new InvalidOperationException(
                    "A semantic directory digest was stored more than once.");
            }
        }
    }

    private sealed record ObservedFile(
        long Length,
        string Sha256,
        string ZigContentDigest);

    private sealed record FinalCommandEvidence(
        IReadOnlyList<string> ArchiveNames,
        IReadOnlyList<string> ArchivePaths,
        IReadOnlyList<string> Frameworks,
        IReadOnlyList<string> SystemLibraries,
        IReadOnlyList<ObservedModule> DerivedModules,
        IReadOnlyList<string> SemanticCommand);

    private sealed record ObservedDerivedFile(
        string Name,
        long Length,
        string Sha256);

    private sealed record ZigManifest(
        IReadOnlyList<ZigManifestRecord> Records);

    private sealed record ZigManifestRecord(
        long Length,
        string ContentDigest,
        int Prefix,
        string Path);

    private sealed record PrimaryPath(
        string LogicalPath,
        string PhysicalPath,
        string TrustRoot,
        bool AllowSdkLinks);

    private sealed record PendingPrimaryFile(
        string LogicalPath,
        long Length,
        string Sha256);

    private sealed record PendingGlobalFile(
        string PackageKey,
        string RelativePath,
        long Length,
        string Sha256);

    private sealed record ObservedPrimaryFile(
        string Path,
        long Length,
        string Sha256);

    private sealed record ObservedClosure(
        IReadOnlyList<ObservedPrimaryFile> Files,
        IReadOnlyList<ObservedDerivedFile> DerivedFiles,
        int FileCount,
        long TotalBytes,
        string DigestSha256,
        int DerivedRecordCount,
        long DerivedBytes);

    private sealed record EvidencePaths(
        string TracePath,
        string RepositoryRoot,
        string GhosttySourceDirectory,
        string ZigExecutablePath,
        string ZigLibraryDirectory,
        string ZigLocalCacheDirectory,
        string ZigGlobalCacheDirectory,
        string SdkDirectory,
        string MetallibPath,
        string InstallDirectory,
        string ArtifactLibGhosttyPath)
    {
        public static EvidencePaths Parse(
            NativeMacOsBuildEvidenceRequest request)
        {
            var paths = new EvidencePaths(
                NormalizeRequiredPath(request.TracePath, nameof(request.TracePath)),
                NormalizeRequiredPath(
                    request.RepositoryRoot,
                    nameof(request.RepositoryRoot)),
                NormalizeRequiredPath(
                    request.GhosttySourceDirectory,
                    nameof(request.GhosttySourceDirectory)),
                NormalizeRequiredPath(
                    request.ZigExecutablePath,
                    nameof(request.ZigExecutablePath)),
                NormalizeRequiredPath(
                    request.ZigLibraryDirectory,
                    nameof(request.ZigLibraryDirectory)),
                NormalizeRequiredPath(
                    request.ZigLocalCacheDirectory,
                    nameof(request.ZigLocalCacheDirectory)),
                NormalizeRequiredPath(
                    request.ZigGlobalCacheDirectory,
                    nameof(request.ZigGlobalCacheDirectory)),
                NormalizeRequiredPath(
                    request.SdkDirectory,
                    nameof(request.SdkDirectory)),
                NormalizeRequiredPath(
                    request.MetallibPath,
                    nameof(request.MetallibPath)),
                NormalizeRequiredPath(
                    request.InstallDirectory,
                    nameof(request.InstallDirectory)),
                NormalizeRequiredPath(
                    request.ArtifactLibGhosttyPath,
                    nameof(request.ArtifactLibGhosttyPath)));
            paths.Validate();
            return paths;
        }

        private void Validate()
        {
            RequireDirectory(RepositoryRoot, "Repository root");
            RequireDirectory(GhosttySourceDirectory, "Ghostty source directory");
            RequireDirectory(ZigLibraryDirectory, "Zig library directory");
            RequireDirectory(
                ZigLocalCacheDirectory,
                "Zig local cache directory");
            RequireDirectory(
                ZigGlobalCacheDirectory,
                "Zig global cache directory");
            RequireDirectory(SdkDirectory, "macOS SDK directory");
            RequireDirectory(InstallDirectory, "Ghostty install directory");

            if (!IsWithin(GhosttySourceDirectory, RepositoryRoot)
                || !IsWithin(ZigExecutablePath, RepositoryRoot)
                || !IsWithin(ZigLibraryDirectory, RepositoryRoot)
                || !IsWithin(MetallibPath, RepositoryRoot)
                || !IsWithin(ArtifactLibGhosttyPath, RepositoryRoot)
                || IsWithin(ZigLocalCacheDirectory, ZigGlobalCacheDirectory)
                || IsWithin(ZigGlobalCacheDirectory, ZigLocalCacheDirectory))
            {
                throw new InvalidDataException(
                    "Native build-evidence roots do not have the required isolation.");
            }

            _ = ObserveRegularFile(
                TracePath,
                Path.GetDirectoryName(TracePath)
                    ?? throw new InvalidDataException(
                        "The native build trace has no directory."),
                allowSdkLinks: false);
            _ = ObserveRegularFile(
                ZigExecutablePath,
                RepositoryRoot,
                allowSdkLinks: false);
            _ = ObserveRegularFile(
                MetallibPath,
                RepositoryRoot,
                allowSdkLinks: false);
        }

        private static string NormalizeRequiredPath(
            string path,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "A native build-evidence path is required.",
                    parameterName);
            }

            return NormalizePath(path);
        }
    }
}
