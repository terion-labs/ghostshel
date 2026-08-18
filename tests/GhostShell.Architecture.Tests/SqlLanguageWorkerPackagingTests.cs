using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed class SqlLanguageWorkerPackagingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DesktopPublishPreservesTheRidScopedWorkerLocation()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj"));
        var worker = Assert.Single(
            project.Descendants("Content"),
            element => string.Equals(
                (string?)element.Attribute("Include"),
                "$(GhostShellSqlLanguageWorkerPath)",
                StringComparison.Ordinal));

        Assert.Equal(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/$(GhostShellSqlLanguageWorkerName)",
            (string?)worker.Attribute("Link"));
        Assert.Equal("PreserveNewest", (string?)worker.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)worker.Attribute("CopyToPublishDirectory"));

        var linkedPayload = project.Descendants("Content")
            .Select(element => (string?)element.Attribute("Link"))
            .Where(link => link is not null)
            .ToArray();
        Assert.Contains(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/THIRD-PARTY-NOTICES.md",
            linkedPayload, StringComparer.Ordinal);
        Assert.Contains(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/runtime-dependencies.txt",
            linkedPayload, StringComparer.Ordinal);
        Assert.Contains(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/build-receipt.json",
            linkedPayload, StringComparer.Ordinal);
    }

    [Fact]
    public void ReleasePublishFailsClosedWithoutTheCompleteSupportedRidPayload()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj"));

        Assert.Equal(
            "false",
            project.Descendants("GhostShellSqlLanguageRequired").Single().Value);
        var validation = project.Descendants("Target")
            .Single(target => string.Equals(
                (string?)target.Attribute("Name"),
                "ValidateSqlLanguagePayload",
                StringComparison.Ordinal));
        Assert.Contains(
            validation.Elements("Error"),
            error => ((string?)error.Attribute("Text"))?.Contains(
                "win-arm64 is unsupported by GraalVM Native Image",
                StringComparison.Ordinal) == true);

        var requiredFiles = project.Descendants("GhostShellSqlLanguageRequiredFile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();
        Assert.Contains("$(GhostShellSqlLanguageWorkerPath)", requiredFiles, StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellSqlLanguageArtifactDirectory)/THIRD-PARTY-NOTICES.md",
            requiredFiles, StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellSqlLanguageArtifactDirectory)/runtime-dependencies.txt",
            requiredFiles, StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellSqlLanguageArtifactDirectory)/build-receipt.json",
            requiredFiles, StringComparer.Ordinal);
    }

    [Fact]
    public void MacPackageRequiresTheWorkerBeforeAndAfterBundling()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "package-macos.sh"));

        Assert.Contains(
            "native/artifacts/osx-arm64",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "sql_language_worker=\"${sql_language_artifact_directory}/ghostshell-sql-language\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/ghostshell-sql-language",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate}/Contents/MacOS/runtimes/osx-arm64/native/ghostshell-sql-language",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/runtime-dependencies.txt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/build-receipt.json",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract sha256 raw",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract abi raw",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract minimumOsVersion raw",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract legalClosureFormatVersion raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract legalDocumentCount raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract legalReviewRequiredCount raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract runtimeDependencyCount raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract runtimeDependenciesSha256 raw -expect string",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract thirdPartyNoticesSha256 raw -expect string",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "darwin-arm64",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Mach-O 64-bit executable arm64",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "LC_BUILD_VERSION",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "sql_language_platform",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "maximum_sql_language_macos_version=\"13.0\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "sql_language_receipt_minos_normalized",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "macos_version_is_at_most",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmp -s \"${sql_language_receipt}\" \"${published_sql_language_receipt}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_directory}/build-receipt.json",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "published_sql_language_dependencies_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "published_sql_language_notices_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_dependencies_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_notices_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:GhostShellSqlLanguageRequired=true",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseConformanceCiCannotSilentlySkipTheNativeWorker()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "database-viewer-integration.yml"));
        var wrapper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "test-database-viewer-integration.sh"));

        Assert.Contains("needs: sql-language-worker", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_RUN_SQL_LANGUAGE_NATIVE: \"1\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_SQL_LANGUAGE_WORKER: ${{ github.workspace }}/native/artifacts/linux-x64/ghostshell-sql-language",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Run the real native client lifecycle",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_SQL_LANGUAGE_WORKER is required when GHOSTSHELL_RUN_SQL_LANGUAGE_NATIVE=1.",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("[[ ! -f \"${worker_path}\" ]]", wrapper, StringComparison.Ordinal);
        Assert.Contains("[[ ! -x \"${worker_path}\" ]]", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableReleaseGateBuildsOnlyCompleteWorkerSupportedRids()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));

        Assert.Contains("rid: [linux-x64, linux-arm64]", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rid: [linux-x64, linux-arm64, win-x64, win-arm64]",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("docker/setup-qemu-action@", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "./scripts/build-sql-language-worker.sh --docker --rid ${{ matrix.rid }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:GhostShellSqlLanguageRequired=true",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Verify SQL intelligence payload in candidate",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("minimumGlibcVersion", workflow, StringComparison.Ordinal);
        Assert.Contains("legalClosureFormatVersion", workflow, StringComparison.Ordinal);
        Assert.Contains("legalDocumentCount", workflow, StringComparison.Ordinal);
        Assert.Contains("legalReviewRequiredCount", workflow, StringComparison.Ordinal);
        Assert.Contains("runtimeDependencyCount", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "all(type == \"number\" and . >= 1 and floor == .)",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".legalReviewRequiredCount | type == \"number\" and . >= 0 and floor == .",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("runtimeDependenciesSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("thirdPartyNoticesSha256", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "sha256sum \"$native_dir/runtime-dependencies.txt\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "sha256sum \"$native_dir/THIRD-PARTY-NOTICES.md\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("sha256sum \"$worker\"", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedProtocolUsesGeneratedJsonMetadata()
    {
        var protocol = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Infrastructure",
            "SqlLanguageWorkerProtocol.cs"));

        Assert.Contains("JsonSerializerContext", protocol, StringComparison.Ordinal);
        Assert.Contains("[JsonSerializable(typeof(WorkerRequestEnvelope))]", protocol, StringComparison.Ordinal);
        Assert.Contains("[JsonSerializable(typeof(WorkerResponseEnvelope))]", protocol, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializerOptions", protocol, StringComparison.Ordinal);
        Assert.DoesNotContain("GetType(", protocol, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotProbeCompilesTheExactProductionClientSources()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "tools",
            "GhostShell.SqlLanguageAotProbe",
            "GhostShell.SqlLanguageAotProbe.csproj"));
        Assert.Equal(
            "true",
            project.Descendants("IsAotCompatible").Single().Value);

        var linkedSources = project.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/CalciteSqlLanguageService.cs",
            linkedSources, StringComparer.Ordinal);
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/CalciteSqlLanguageSession.cs",
            linkedSources, StringComparer.Ordinal);
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/SqlLanguageWorkerProtocol.cs",
            linkedSources, StringComparer.Ordinal);
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/UnavailableSqlLanguageSession.cs",
            linkedSources, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Unable to locate the GhostSHELL repository root.");
    }
}
