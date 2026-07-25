using System.Text;
using System.Text.Json;
using GhostShell.Packaging;

namespace GhostShell.AccessibilityAcceptance;

public sealed class NativeMacOsBuildEvidenceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-native-build-evidence-{Guid.NewGuid():N}");

    public NativeMacOsBuildEvidenceTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Relocated_fresh_builds_emit_identical_path_free_evidence()
    {
        var first = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "first"),
            "first-cache-key");
        var second = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "second"),
            "second-cache-key");

        var firstResult =
            NativeMacOsBuildEvidenceBuilder.Observe(first.Request);
        var secondResult =
            NativeMacOsBuildEvidenceBuilder.Observe(second.Request);

        Assert.Equal(firstResult.CanonicalJson, secondResult.CanonicalJson);
        Assert.Equal(firstResult.Sha256, secondResult.Sha256);
        var json = StrictUtf8(firstResult.CanonicalJson);
        Assert.DoesNotContain(first.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("first-cache-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("second-cache-key", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(firstResult.CanonicalJson);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "GhostShell.Packaging.NativeMacOsBuildEvidence-2.0.0",
            root.GetProperty("generator").GetString());
        Assert.Equal(12, root.GetProperty("archiveNames").GetArrayLength());
        var semanticCommand = root.GetProperty("semanticCommand")
            .EnumerateArray()
            .Select(token => token.GetString()!)
            .ToArray();
        Assert.Contains("-mcpu", semanticCommand);
        Assert.Contains("baseline", semanticCommand);
        Assert.Contains(
            "zig-local-cache:archive:libfreetype.a",
            semanticCommand);
        var modules = root.GetProperty("derivedModules")
            .EnumerateArray()
            .ToDictionary(
                module => module.GetProperty("name").GetString()!,
                module => module.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
        Assert.Contains("build_options", modules);
        Assert.Contains("terminal_options", modules);
        Assert.NotEqual(
            modules["build_options"],
            modules["terminal_options"]);
        Assert.DoesNotContain(
            root.GetProperty("compileClosure")
                .GetProperty("derivedFiles")
                .EnumerateArray(),
            file => file.GetProperty("name")
                .GetString()!
                .EndsWith(".a", StringComparison.Ordinal));
        Assert.All(
            root.GetProperty("compileClosure")
                .GetProperty("files")
                .EnumerateArray(),
            file => Assert.False(
                Path.IsPathRooted(file.GetProperty("path").GetString())));
    }

    [Fact]
    public void Every_literal_final_command_value_changes_canonical_evidence()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "literal-command"),
            "literal-command-key");
        var baseline = NativeMacOsBuildEvidenceBuilder.Observe(
            fixture.Request);
        fixture.ReplaceTraceToken("baseline", "native");

        var mutated = NativeMacOsBuildEvidenceBuilder.Observe(
            fixture.Request);

        Assert.NotEqual(baseline.Sha256, mutated.Sha256);
        Assert.NotEqual(baseline.CanonicalJson, mutated.CanonicalJson);
    }

    [Fact]
    public void Distinct_cache_include_directories_have_content_identities()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "cache-directory"),
            "cache-directory-key");
        var baseline = NativeMacOsBuildEvidenceBuilder.Observe(
            fixture.Request);
        var alternate = Path.Combine(
            fixture.LocalCacheDirectory,
            "o",
            "alternate-include");
        Directory.CreateDirectory(alternate);
        File.WriteAllText(
            Path.Combine(alternate, "semantic.h"),
            "different include content",
            new UTF8Encoding(false));
        fixture.ReplaceTraceToken(
            fixture.LocalIncludeDirectory,
            alternate);

        var mutated = NativeMacOsBuildEvidenceBuilder.Observe(
            fixture.Request);

        Assert.NotEqual(baseline.Sha256, mutated.Sha256);
    }

    [Fact]
    public void Response_file_syntax_is_not_accepted_as_an_inert_literal()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "response-file"),
            "response-file-key");
        fixture.AppendFinalCommandToken("@arguments.rsp");

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Semantic_directory_byte_limit_is_checked_before_file_hashing()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "semantic-directory-limit"),
            "semantic-directory-limit-key");
        var oversized = Path.Combine(
            fixture.LocalIncludeDirectory,
            "oversized-sparse.h");
        using (var stream = new FileStream(
                   oversized,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(512L * 1024 * 1024 + 1);
        }

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Duplicate_matching_compile_manifest_is_rejected_as_stale_ambiguity()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "ambiguous"),
            "ambiguous-key");
        File.Copy(
            fixture.CompileManifestPath,
            Path.Combine(
                fixture.LocalCacheDirectory,
                "h",
                new string('c', 32) + ".txt"));

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Packaged_output_must_match_the_compile_and_install_edge()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "output-mismatch"),
            "output-key");
        File.AppendAllText(
            fixture.Request.ArtifactLibGhosttyPath,
            "changed",
            Encoding.UTF8);

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Theory]
    [InlineData("malformed-quote")]
    [InlineData("oversize")]
    [InlineData("path-escape")]
    public void Malformed_or_unbounded_external_evidence_is_rejected(
        string mutation)
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, mutation),
            mutation + "-key");
        switch (mutation)
        {
            case "malformed-quote":
                File.WriteAllText(
                    fixture.Request.TracePath,
                    "\"unterminated\n"
                    + File.ReadAllText(fixture.Request.TracePath),
                    Encoding.UTF8);
                break;
            case "oversize":
                File.WriteAllText(
                    fixture.Request.TracePath,
                    new string('x', 8 * 1024 * 1024 + 1),
                    Encoding.UTF8);
                break;
            case "path-escape":
                var escaped = Path.Combine(fixture.Root, "escaped-source.zig");
                File.WriteAllText(escaped, "escaped", Encoding.UTF8);
                fixture.AppendCompileManifestRecord(
                    escaped,
                    prefix: 0,
                    manifestPath: escaped);
                break;
            default:
                throw new InvalidOperationException();
        }

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Source_mutation_after_the_compile_manifest_is_rejected()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "source-mutation"),
            "source-key");
        File.AppendAllText(
            fixture.MainSourcePath,
            "post-build mutation",
            Encoding.UTF8);

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Same_length_source_mutation_after_the_manifest_is_rejected()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "same-length-source-mutation"),
            "same-length-source-key");
        var original = File.ReadAllBytes(fixture.MainSourcePath);
        File.WriteAllBytes(
            fixture.MainSourcePath,
            Enumerable.Repeat((byte)'x', original.Length).ToArray());

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Archive_mutation_after_the_compile_manifest_is_rejected()
    {
        var fixture = NativeBuildEvidenceFixture.Create(
            Path.Combine(_temporaryDirectory, "archive-mutation"),
            "archive-mutation-key");
        File.AppendAllText(
            fixture.ArchivePaths[0],
            "post-build archive mutation",
            Encoding.UTF8);

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsBuildEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Zig_manifest_digest_matches_the_pinned_zig_empty_file_vector()
    {
        Assert.Equal(
            "82547a8dd7f3efb3f077622e34876868",
            ZigManifestContentDigest.Compute([]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static string StrictUtf8(byte[] content) =>
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(content);
}

internal sealed class NativeBuildEvidenceFixture
{
    private static readonly string[] ArchiveNames =
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

    private NativeBuildEvidenceFixture(
        string root,
        NativeMacOsBuildEvidenceRequest request,
        string localCacheDirectory,
        string compileManifestPath,
        string mainSourcePath,
        string localIncludeDirectory,
        IReadOnlyList<string> archivePaths)
    {
        Root = root;
        Request = request;
        LocalCacheDirectory = localCacheDirectory;
        CompileManifestPath = compileManifestPath;
        MainSourcePath = mainSourcePath;
        LocalIncludeDirectory = localIncludeDirectory;
        ArchivePaths = archivePaths;
    }

    public string Root { get; }

    public NativeMacOsBuildEvidenceRequest Request { get; }

    public string LocalCacheDirectory { get; }

    public string CompileManifestPath { get; }

    public string MainSourcePath { get; }

    public string LocalIncludeDirectory { get; }

    public IReadOnlyList<string> ArchivePaths { get; }

    public static NativeBuildEvidenceFixture Create(
        string root,
        string globalPackageKey)
    {
        var repository = Path.Combine(root, "repository");
        var ghostty = Path.Combine(repository, "vendor", "ghostty");
        var zigExecutable = Path.Combine(repository, "toolchain", "zig");
        var zigLibrary = Path.Combine(repository, "toolchain", "lib");
        var localCache = Path.Combine(root, "local-cache");
        var globalCache = Path.Combine(repository, "zig-global-cache");
        var sdk = Path.Combine(root, "MacOSX.sdk");
        var metallib = Path.Combine(
            repository,
            "derived",
            "Ghostty.metallib");
        var install = Path.Combine(root, "install");
        var artifact = Path.Combine(
            repository,
            "native",
            "artifacts",
            "libghostty.dylib");
        var trace = Path.Combine(root, "zig-trace.log");

        Directory.CreateDirectory(root);
        WriteFile(zigExecutable, "pinned zig executable");
        WriteFile(
            Path.Combine(zigLibrary, "compiler", "build_runner.zig"),
            "build runner");
        WriteFile(
            Path.Combine(zigLibrary, "std", "std.zig"),
            "standard library");
        WriteFile(Path.Combine(ghostty, "build.zig"), "build graph");
        WriteFile(Path.Combine(ghostty, "build.zig.zon"), "dependencies");
        WriteFile(
            Path.Combine(ghostty, "src", "build", "SharedDeps.zig"),
            "shared deps");
        WriteFile(
            Path.Combine(ghostty, "src", "build", "GhosttyLib.zig"),
            "ghostty lib");
        var mainSource = Path.Combine(ghostty, "src", "main_c.zig");
        WriteFile(mainSource, "pub export fn ghostty_init() void {}");
        WriteFile(Path.Combine(ghostty, "src", "render.zig"), "render");
        WriteFile(metallib, "metallib");

        var frameworkDirectory = Path.Combine(
            sdk,
            "System",
            "Library",
            "Frameworks");
        Directory.CreateDirectory(frameworkDirectory);
        Directory.CreateDirectory(Path.Combine(sdk, "usr", "include"));
        var sdkLibrary = Path.Combine(sdk, "usr", "lib", "libc.tbd");
        WriteFile(sdkLibrary, "sdk libc");

        var packageSource = Path.Combine(
            globalCache,
            "p",
            globalPackageKey,
            "pkg",
            "main.zig");
        WriteFile(packageSource, "global package");

        var archives = new List<string>();
        for (var i = 0; i < ArchiveNames.Length; i++)
        {
            var archive = Path.Combine(
                localCache,
                "o",
                $"archive-{i:D2}",
                ArchiveNames[i]);
            WriteFile(archive, $"archive {ArchiveNames[i]}");
            archives.Add(archive);
        }

        var buildOptions = Path.Combine(
            localCache,
            "c",
            "build-options",
            "options.zig");
        var terminalOptions = Path.Combine(
            localCache,
            "c",
            "terminal-options",
            "options.zig");
        WriteFile(buildOptions, "build options");
        WriteFile(terminalOptions, "terminal options");
        var localInclude = Path.Combine(
            localCache,
            "o",
            "semantic-include");
        WriteFile(
            Path.Combine(localInclude, "semantic.h"),
            "semantic include content");
        var compiledOutput = Path.Combine(
            localCache,
            "o",
            "ghostty-output",
            "libghostty.dylib");
        WriteFile(compiledOutput, "compiled libghostty");
        var installedOutput = Path.Combine(
            install,
            "lib",
            "libghostty.dylib");
        CopyFile(compiledOutput, installedOutput);
        CopyFile(compiledOutput, artifact);

        var manifestDirectory = Path.Combine(localCache, "h");
        Directory.CreateDirectory(manifestDirectory);
        var compileManifest = Path.Combine(
            manifestDirectory,
            new string('a', 32) + ".txt");
        var compileRecords = new List<ManifestFixtureRecord>
        {
            RelativeRecord(repository, mainSource, prefix: 0),
            RelativeRecord(repository, metallib, prefix: 0),
            RelativeRecord(
                repository,
                Path.Combine(ghostty, "src", "render.zig"),
                prefix: 0),
            RelativeRecord(zigLibrary, Path.Combine(
                zigLibrary,
                "std",
                "std.zig"), prefix: 1),
            RelativeRecord(globalCache, packageSource, prefix: 3),
            new(sdkLibrary, 0, sdkLibrary),
            RelativeRecord(localCache, buildOptions, prefix: 2),
            RelativeRecord(localCache, terminalOptions, prefix: 2),
        };
        compileRecords.AddRange(archives.Select(
            archive => RelativeRecord(localCache, archive, prefix: 2)));
        WriteManifest(compileManifest, compileRecords);

        var buildGraphManifest = Path.Combine(
            manifestDirectory,
            new string('b', 32) + ".txt");
        WriteManifest(
            buildGraphManifest,
            [
                RelativeRecord(
                    repository,
                    Path.Combine(ghostty, "build.zig"),
                    prefix: 0),
                RelativeRecord(
                    repository,
                    Path.Combine(ghostty, "build.zig.zon"),
                    prefix: 0),
                RelativeRecord(
                    repository,
                    Path.Combine(
                        ghostty,
                        "src",
                        "build",
                        "SharedDeps.zig"),
                    prefix: 0),
                RelativeRecord(
                    repository,
                    Path.Combine(
                        ghostty,
                        "src",
                        "build",
                        "GhosttyLib.zig"),
                    prefix: 0),
                RelativeRecord(
                    zigLibrary,
                    Path.Combine(
                        zigLibrary,
                        "compiler",
                        "build_runner.zig"),
                    prefix: 1),
            ]);
        File.WriteAllText(
            Path.Combine(
                manifestDirectory,
                new string('d', 32) + ".txt"),
            "0\n",
            new UTF8Encoding(false));

        List<string> command =
        [
            Path.GetRelativePath(repository, zigExecutable),
            "build-lib",
            .. archives,
            archives[0],
            "-OReleaseFast",
            "-target",
            "aarch64-macos.13.0",
            "-mcpu",
            "baseline",
            "-I",
            localInclude,
            "-Mroot=" + mainSource,
            "-ODebug",
            "-Mbuild_options=" + buildOptions,
            "-Mterminal_options=" + terminalOptions,
            "-Mghostty_metallib=" + metallib,
            "-Mpackage=" + Path.GetRelativePath(repository, packageSource),
            "-M1x1#000000.png="
                + Path.GetRelativePath(repository, packageSource),
            "-framework",
            "Cocoa",
            "-framework",
            "Metal",
            "-lobjc",
            "-lc",
            "-iframework",
            frameworkDirectory,
            "-isystem",
            Path.Combine(sdk, "usr", "include"),
            "-L",
            Path.Combine(sdk, "usr", "lib"),
            "--cache-dir",
            localCache,
            "--global-cache-dir",
            Path.GetRelativePath(repository, globalCache),
            "--zig-lib-dir",
            Path.GetRelativePath(repository, zigLibrary),
            "--name",
            "ghostty",
            "-dynamic",
            "-install_name",
            "@rpath/libghostty.dylib",
        ];
        var traceText = string.Join(' ', command.Select(Quote))
            + "\ninstall -C "
            + Quote(compiledOutput)
            + " "
            + Quote(installedOutput)
            + "\n";
        File.WriteAllText(trace, traceText, new UTF8Encoding(false));

        var request = new NativeMacOsBuildEvidenceRequest(
            trace,
            repository,
            ghostty,
            zigExecutable,
            zigLibrary,
            localCache,
            globalCache,
            sdk,
            metallib,
            install,
            artifact);
        return new NativeBuildEvidenceFixture(
            root,
            request,
            localCache,
            compileManifest,
            mainSource,
            localInclude,
            archives);
    }

    public void ReplaceTraceToken(string original, string replacement)
    {
        var originalToken = Quote(original);
        var replacementToken = Quote(replacement);
        var trace = File.ReadAllText(Request.TracePath, Encoding.UTF8);
        var mutated = trace.Replace(
            originalToken,
            replacementToken,
            StringComparison.Ordinal);
        if (mutated == trace)
        {
            throw new InvalidOperationException(
                "The requested trace token was not present.");
        }

        File.WriteAllText(
            Request.TracePath,
            mutated,
            new UTF8Encoding(false));
    }

    public void AppendFinalCommandToken(string token)
    {
        var lines = File.ReadAllLines(Request.TracePath, Encoding.UTF8);
        lines[0] += " " + Quote(token);
        File.WriteAllLines(
            Request.TracePath,
            lines,
            new UTF8Encoding(false));
    }

    public void AppendCompileManifestRecord(
        string physicalPath,
        int prefix,
        string manifestPath)
    {
        var record = new ManifestFixtureRecord(
            physicalPath,
            prefix,
            manifestPath);
        File.AppendAllText(
            CompileManifestPath,
            FormatManifestRecord(record),
            new UTF8Encoding(false));
    }

    private static void WriteManifest(
        string path,
        IReadOnlyList<ManifestFixtureRecord> records)
    {
        var builder = new StringBuilder("0\n");
        foreach (var record in records)
        {
            builder.Append(FormatManifestRecord(record));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static string FormatManifestRecord(
        ManifestFixtureRecord record)
    {
        var digest = ZigManifestContentDigest.Compute(
            File.ReadAllBytes(record.PhysicalPath));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{new FileInfo(record.PhysicalPath).Length} 1 1 "
            + $"{digest} {record.Prefix} "
            + $"{record.ManifestPath.Replace(Path.DirectorySeparatorChar, '/')}\n");
    }

    private static ManifestFixtureRecord RelativeRecord(
        string root,
        string physicalPath,
        int prefix) =>
        new(
            physicalPath,
            prefix,
            Path.GetRelativePath(root, physicalPath));

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void CopyFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
    }

    private static string Quote(string token) =>
        "\""
        + token.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
        + "\"";

    private sealed record ManifestFixtureRecord(
        string PhysicalPath,
        int Prefix,
        string ManifestPath);
}
