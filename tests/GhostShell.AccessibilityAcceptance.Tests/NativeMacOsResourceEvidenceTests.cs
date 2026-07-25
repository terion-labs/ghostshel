using System.Text;
using System.Text.Json;
using GhostShell.Packaging;

namespace GhostShell.AccessibilityAcceptance;

public sealed class NativeMacOsResourceEvidenceTests : IDisposable
{
    private const string ExpectedThemePackageKey =
        "N-V-__8AABVbAwBwDRyZONfx553tvMW8_A2OKUoLzPUSRiLF";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-resource-evidence-tests-{Guid.NewGuid():N}");

    public NativeMacOsResourceEvidenceTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Evidence_is_deterministic_and_path_free_after_root_relocation()
    {
        var first = ResourceFixture.Create(
            Path.Combine(_temporaryDirectory, "first"),
            ExpectedThemePackageKey);
        var second = ResourceFixture.Create(
            Path.Combine(_temporaryDirectory, "second"),
            ExpectedThemePackageKey);

        var firstEvidence = NativeMacOsResourceEvidenceBuilder.Observe(
            first.Request);
        var secondEvidence = NativeMacOsResourceEvidenceBuilder.Observe(
            second.Request);

        Assert.Equal(firstEvidence.RawContent, secondEvidence.RawContent);
        Assert.Equal(firstEvidence.Sha256, secondEvidence.Sha256);
        var json = Encoding.UTF8.GetString(firstEvidence.RawContent);
        Assert.DoesNotContain(first.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Root, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("changed-shell")]
    [InlineData("missing-shell")]
    [InlineData("extra-shell")]
    [InlineData("changed-theme")]
    [InlineData("missing-theme")]
    [InlineData("extra-theme")]
    public void Evidence_rejects_changed_missing_and_extra_runtime_files(
        string mutation)
    {
        var fixture = ResourceFixture.Create(
            Path.Combine(_temporaryDirectory, mutation),
            ExpectedThemePackageKey);
        fixture.Mutate(mutation);

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsResourceEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Evidence_rejects_byte_exact_themes_under_the_wrong_package_key()
    {
        var fixture = ResourceFixture.Create(
            Path.Combine(_temporaryDirectory, "wrong-theme-key"),
            "unexpected-theme-package-key");

        var exception = Assert.Throws<InvalidDataException>(() =>
            NativeMacOsResourceEvidenceBuilder.Observe(fixture.Request));

        Assert.Contains(
            "unexpected Zig package key",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_rejects_symbolic_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = ResourceFixture.Create(
            Path.Combine(_temporaryDirectory, "symbolic-link"),
            ExpectedThemePackageKey);
        var installedPath = Path.Combine(
            fixture.InstallDirectory,
            "share",
            "ghostty",
            "shell-integration",
            "bash",
            "ghostty.bash");
        File.Delete(installedPath);
        File.CreateSymbolicLink(
            installedPath,
            Path.Combine(
                fixture.GhosttySourceDirectory,
                "src",
                "shell-integration",
                "bash",
                "ghostty.bash"));

        Assert.Throws<InvalidDataException>(() =>
            NativeMacOsResourceEvidenceBuilder.Observe(fixture.Request));
    }

    [Fact]
    public void Terminfo_is_labeled_as_an_observation_with_blocked_provenance()
    {
        var fixture = ResourceFixture.Create(
            Path.Combine(_temporaryDirectory, "terminfo-observation"),
            ExpectedThemePackageKey);

        var evidence = NativeMacOsResourceEvidenceBuilder.Observe(
            fixture.Request);
        using var document = JsonDocument.Parse(evidence.RawContent);
        var root = document.RootElement;
        var terminfo = root.GetProperty("terminfo");

        Assert.Equal("BLOCKED", root.GetProperty("releaseReadiness").GetString());
        Assert.False(root.GetProperty("legalClearance").GetBoolean());
        Assert.Equal("NOT_ASSERTED", root.GetProperty("legalConclusion").GetString());
        Assert.Equal(
            "artifact-observation",
            terminfo.GetProperty("evidenceKind").GetString());
        Assert.Equal(
            "VERIFIED",
            terminfo.GetProperty("artifactObservationStatus").GetString());
        Assert.Equal(
            "BLOCKED",
            terminfo.GetProperty("sourceProvenanceStatus").GetString());
        Assert.Equal(
            "BLOCKED",
            terminfo.GetProperty("toolProvenanceStatus").GetString());
        Assert.Equal(2, terminfo.GetProperty("fileCount").GetInt32());
        Assert.Equal(
            ["67/ghostty", "78/xterm-ghostty"],
            terminfo
                .GetProperty("files")
                .EnumerateArray()
                .Select(file =>
                    file.GetProperty("path").GetString()
                    ?? throw new InvalidDataException(
                        "The evidence file path must be a string."))
                .ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class ResourceFixture
    {
        private static readonly string[] ShellPaths =
        [
            "bash/bash-preexec.sh",
            "bash/ghostty.bash",
            "elvish/lib/ghostty-integration.elv",
            "fish/vendor_conf.d/ghostty-shell-integration.fish",
            "nushell/vendor/autoload/ghostty.nu",
            "zsh/.zshenv",
            "zsh/ghostty-integration",
        ];

        private ResourceFixture(
            string root,
            string ghosttySourceDirectory,
            string zigGlobalCacheDirectory,
            string installDirectory)
        {
            Root = root;
            GhosttySourceDirectory = ghosttySourceDirectory;
            ZigGlobalCacheDirectory = zigGlobalCacheDirectory;
            InstallDirectory = installDirectory;
            Request = new NativeMacOsResourceEvidenceRequest(
                ghosttySourceDirectory,
                zigGlobalCacheDirectory,
                installDirectory);
        }

        public string Root { get; }

        public string GhosttySourceDirectory { get; }

        public string ZigGlobalCacheDirectory { get; }

        public string InstallDirectory { get; }

        public NativeMacOsResourceEvidenceRequest Request { get; }

        public static ResourceFixture Create(string root, string themePackageKey)
        {
            var ghosttySource = Path.Combine(root, "ghostty-source");
            var zigGlobalCache = Path.Combine(root, "zig-global-cache");
            var install = Path.Combine(root, "ghostty-install");
            Directory.CreateDirectory(ghosttySource);
            Directory.CreateDirectory(Path.Combine(zigGlobalCache, "p"));
            Directory.CreateDirectory(install);

            var sourceShell = Path.Combine(
                ghosttySource,
                "src",
                "shell-integration");
            var installedShell = Path.Combine(
                install,
                "share",
                "ghostty",
                "shell-integration");
            foreach (var path in ShellPaths)
            {
                WriteFile(sourceShell, path, $"shell:{path}\n");
                WriteFile(installedShell, path, $"shell:{path}\n");
            }

            WriteFile(sourceShell, "README.md", "not installed\n");
            WriteFile(
                sourceShell,
                "development-only.sh",
                "also not installed\n");

            var installedThemes = Path.Combine(
                install,
                "share",
                "ghostty",
                "themes");
            var sourceThemes = Path.Combine(
                zigGlobalCache,
                "p",
                themePackageKey);
            for (var index = 0; index < 463; index++)
            {
                var path = $"theme-{index:000}";
                var content = $"theme:{index:000}\n";
                WriteFile(installedThemes, path, content);
                WriteFile(sourceThemes, path, content);
            }

            var terminfo = Path.Combine(install, "share", "terminfo");
            WriteFile(terminfo, "67/ghostty", "compiled ghostty\n");
            WriteFile(
                terminfo,
                "78/xterm-ghostty",
                "compiled xterm-ghostty\n");

            return new ResourceFixture(
                root,
                ghosttySource,
                zigGlobalCache,
                install);
        }

        public void Mutate(string mutation)
        {
            var installedShell = Path.Combine(
                InstallDirectory,
                "share",
                "ghostty",
                "shell-integration");
            var installedThemes = Path.Combine(
                InstallDirectory,
                "share",
                "ghostty",
                "themes");
            switch (mutation)
            {
                case "changed-shell":
                    File.AppendAllText(
                        Path.Combine(installedShell, "bash", "ghostty.bash"),
                        "changed\n");
                    break;
                case "missing-shell":
                    File.Delete(Path.Combine(
                        installedShell,
                        "bash",
                        "ghostty.bash"));
                    break;
                case "extra-shell":
                    WriteFile(installedShell, "bash/extra.sh", "extra\n");
                    break;
                case "changed-theme":
                    File.AppendAllText(
                        Path.Combine(installedThemes, "theme-000"),
                        "changed\n");
                    break;
                case "missing-theme":
                    File.Delete(Path.Combine(installedThemes, "theme-000"));
                    break;
                case "extra-theme":
                    WriteFile(installedThemes, "theme-extra", "extra\n");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown fixture mutation {mutation}.");
            }
        }

        private static void WriteFile(
            string root,
            string relativePath,
            string content)
        {
            var path = Path.Combine(
                root,
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
