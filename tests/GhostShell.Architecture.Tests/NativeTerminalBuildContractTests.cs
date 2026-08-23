using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed partial class NativeTerminalBuildContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Bootstrap_selects_the_actual_supported_host_runtime()
    {
        var script = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "bootstrap.sh"));

        Assert.Contains("Darwin:arm64)", script, StringComparison.Ordinal);
        Assert.Contains("native_rid=\"osx-arm64\"", script, StringComparison.Ordinal);
        Assert.Contains("Darwin:x86_64)", script, StringComparison.Ordinal);
        Assert.Contains("native_rid=\"osx-x64\"", script, StringComparison.Ordinal);
        Assert.Contains("Linux:aarch64|Linux:arm64)", script, StringComparison.Ordinal);
        Assert.Contains("native_rid=\"linux-arm64\"", script, StringComparison.Ordinal);
        Assert.Contains("Linux:x86_64)", script, StringComparison.Ordinal);
        Assert.Contains("native_rid=\"linux-x64\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "build-libghostty-vt.sh\" --rid \"${native_rid}\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Native_build_supports_the_pinned_windows_host_toolchain()
    {
        var script = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "build-libghostty-vt.sh"));

        Assert.Contains("MINGW*:*64|MSYS*:*64|CYGWIN*:*64)", script, StringComparison.Ordinal);
        Assert.Contains("zig_distribution=\"x86_64-windows\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e",
            script,
            StringComparison.Ordinal);
        Assert.Contains("zig_archive_extension=\"zip\"", script, StringComparison.Ordinal);
        Assert.Contains("zig_executable=\"zig.exe\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_build_tests_the_patched_source_and_gates_published_exports()
    {
        var script = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "build-libghostty-vt.sh"));
        var testIndex = script.IndexOf(
            "\"${zig}\" build test-lib-vt",
            StringComparison.Ordinal);
        var publishIndex = script.IndexOf(
            "native-publish-artifacts",
            StringComparison.Ordinal);

        Assert.True(testIndex >= 0, "The native build must run the patched Zig tests.");
        Assert.True(
            publishIndex > testIndex,
            "Native publication must happen only after the patched Zig tests.");
        Assert.Contains("required_exports_manifest", script, StringComparison.Ordinal);
        Assert.Contains("nm -g -P", script, StringComparison.Ordinal);
        Assert.Contains("missing required export", script, StringComparison.Ordinal);
        Assert.Contains("extension-abi-probe.c", script, StringComparison.Ordinal);
        Assert.Contains(
            "${abi_probe_runtime_dir}/libghostty-vt.so.${library_version%%.*}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Wl,-rpath,${abi_probe_runtime_dir}",
            script,
            StringComparison.Ordinal);
        Assert.Contains("-std=c11", script, StringComparison.Ordinal);
        Assert.Contains("-Wall", script, StringComparison.Ordinal);
        Assert.Contains("-Wextra", script, StringComparison.Ordinal);
        Assert.Contains("-Werror", script, StringComparison.Ordinal);
        Assert.Contains("testsPassed", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_font_assets_are_fetched_from_the_exact_ghostty_pin()
    {
        var script = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "scripts",
                "build-terminal-font-assets.sh"));
        var fetchIndex = script.IndexOf("\"${zig}\" fetch", StringComparison.Ordinal);
        var publishIndex = script.IndexOf(
            "native-publish-artifacts",
            StringComparison.Ordinal);

        Assert.Contains(
            "https://deps.files.ghostty.org/JetBrainsMono-2.304.tar.gz",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "N-V-__8AAIC5lwAVPJJzxnCAahSvZTIlG-HhtOvnM1uh-66x",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "08f039fbb3dea9c6b1cdb5ff4550666598122346",
            script,
            StringComparison.Ordinal);
        Assert.Contains("JetBrainsMono-Regular.ttf", script, StringComparison.Ordinal);
        Assert.Contains("JetBrainsMono-Bold.ttf", script, StringComparison.Ordinal);
        Assert.Contains("JetBrainsMono-Italic.ttf", script, StringComparison.Ordinal);
        Assert.Contains("JetBrainsMono-BoldItalic.ttf", script, StringComparison.Ordinal);
        Assert.Contains("OFL.txt", script, StringComparison.Ordinal);
        Assert.Contains("MANIFEST.sha256", script, StringComparison.Ordinal);
        Assert.Contains(
            "terminal-font-assets-build-receipt.json",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "${artifact_parent_dir}/common",
            script,
            StringComparison.Ordinal);
        Assert.True(fetchIndex >= 0, "The official pinned font package must be fetched by Zig.");
        Assert.True(
            publishIndex > fetchIndex,
            "Common font assets must publish only after their fetch and verification gates.");
    }

    [Fact]
    public void Native_runtime_build_gates_publication_on_common_font_assets()
    {
        var script = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "build-libghostty-vt.sh"));
        var nativeTestIndex = script.IndexOf(
            "\"${zig}\" build test-lib-vt",
            StringComparison.Ordinal);
        var fontBuildIndex = script.IndexOf(
            "build-terminal-font-assets.sh",
            StringComparison.Ordinal);
        var nativePublishIndex = script.IndexOf(
            "native-publish-artifacts",
            StringComparison.Ordinal);

        Assert.True(fontBuildIndex > nativeTestIndex);
        Assert.True(nativePublishIndex > fontBuildIndex);
    }

    [Fact]
    public void Repository_gate_runs_only_for_version_tags()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));

        Assert.Contains(
            "on:\n  push:\n    tags: [\"v*\"]",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("branches:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  workflow_dispatch:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_gate_provisions_macos_native_assets_before_managed_builds()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));

        Assert.Contains("terminal-native-assets:", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "name: terminal-font-test-assets",
            workflow,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            workflow.Split(
                "needs: terminal-native-assets",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            3,
            workflow.Split(
                "name: Download terminal font assets",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("name: terminal-native-osx-arm64", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("name: terminal-native-linux-x64", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("name: terminal-native-win-x64", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "uses: actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "if: steps.terminal-cache.outputs.cache-hit != 'true'",
            workflow,
            StringComparison.Ordinal);
        Assert.Equal(
            workflow.Split(
                "uses: actions/setup-dotnet@",
                StringSplitOptions.None).Length - 1,
            workflow.Split(
                "name: Expose the SDK at the repository-local path",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            workflow.Split(
                "./scripts/build-cef-runtime.sh --rid osx-arm64",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "path: native/artifacts/osx-arm64\n" +
            "          if-no-files-found: error\n" +
            "          retention-days: 1\n" +
            "          include-hidden-files: true",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void App_embeds_the_exact_terminal_faces_and_desktop_publishes_the_license()
    {
        var appProject = XDocument.Load(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.App",
                "GhostShell.App.csproj"));
        var resources = appProject
            .Descendants("GhostShellTerminalFontFile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .Cast<string>()
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var resourceLink = Assert.Single(
            appProject.Descendants("AvaloniaResource"),
            element => string.Equals(
                (string?)element.Attribute("Include"),
                "@(GhostShellTerminalFontFile)",
                StringComparison.Ordinal));
        var validation = Assert.Single(
            appProject.Descendants("Target"),
            element => string.Equals(
                (string?)element.Attribute("Name"),
                "ValidateGhostShellTerminalFonts",
                StringComparison.Ordinal));
        var desktopProjectText = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));

        Assert.Equal(
            [
                "JetBrainsMono-Bold.ttf",
                "JetBrainsMono-BoldItalic.ttf",
                "JetBrainsMono-Italic.ttf",
                "JetBrainsMono-Regular.ttf",
            ],
            resources);
        Assert.Equal(
            "Assets/Fonts/JetBrainsMono/%(Filename)%(Extension)",
            (string?)resourceLink.Attribute("Link"));
        Assert.Contains(
            validation.Descendants("Error"),
            error => ((string?)error.Attribute("Condition"))?.Contains(
                "GhostShellTerminalFontLicenseFile",
                StringComparison.Ordinal) == true);
        Assert.Contains("JETBRAINS-MONO-OFL.txt", desktopProjectText, StringComparison.Ordinal);
        Assert.Contains("CopyToPublishDirectory=\"PreserveNewest\"", desktopProjectText, StringComparison.Ordinal);
    }

    [Fact]
    public void Reviewed_export_manifest_covers_every_managed_import()
    {
        var terminalSource = Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Terminal",
            "GhosttyVt");
        var imported = Directory
            .EnumerateFiles(terminalSource, "GhosttyVtNative*.cs")
            .SelectMany(path => EntryPointPattern().Matches(File.ReadAllText(path)))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        imported.Add("ghostty_ghostshell_extension_abi");
        var reviewed = File.ReadAllLines(
                Path.Combine(
                    RepositoryRoot,
                    "native",
                    "ghostty-vt",
                    "required-exports.txt"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(imported);
        Assert.Equal(
            imported.Order(StringComparer.Ordinal),
            reviewed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Managed_abi_validation_roots_static_native_aot_marshalling_layouts()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Terminal",
            "GhosttyVt",
            "GhosttyVtAbi.cs"));
        var rootedLayouts = GenericMarshalSizePattern()
            .Matches(source)
            .Select(match => match.Groups["type"].Value)
            .ToArray();

        Assert.Equal(26, rootedLayouts.Length);
        Assert.Equal(
            rootedLayouts.Length,
            rootedLayouts.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("GhosttyVtString", rootedLayouts, StringComparer.Ordinal);
        Assert.Contains(
            "GhosttyVtKittyVirtualPlacementRenderInfo",
            rootedLayouts,
            StringComparer.Ordinal);
        Assert.DoesNotContain("Marshal.SizeOf(type)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_requires_the_cef_payload_only_when_publishing()
    {
        var project = XDocument.Load(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));
        var validation = Assert.Single(
            project.Descendants("Target"),
            element => string.Equals(
                (string?)element.Attribute("Name"),
                "ValidateCefRuntimePayload",
                StringComparison.Ordinal));
        var optionalBuildShim = Assert.Single(
            project.Descendants("Content"),
            element => string.Equals(
                (string?)element.Attribute("Link"),
                "$(GhostShellCefShimLibrary)",
                StringComparison.Ordinal));

        Assert.Equal("Publish", (string?)validation.Attribute("BeforeTargets"));
        Assert.Equal(
            "Exists('$(GhostShellCefRuntimeArtifactDirectory)/$(GhostShellCefShimLibrary)')",
            (string?)optionalBuildShim.Attribute("Condition"));
    }

    [Fact]
    public void Desktop_build_fails_closed_for_every_supported_native_payload()
    {
        var project = XDocument.Load(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));
        var requiredFiles = project
            .Descendants("GhostShellNativeTerminalRequiredFile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .Cast<string>()
            .ToArray();
        var validation = Assert.Single(
            project.Descendants("Target"),
            element => string.Equals(
                (string?)element.Attribute("Name"),
                "ValidateNativeTerminalPayload",
                StringComparison.Ordinal));
        var error = Assert.Single(validation.Descendants("Error"));

        Assert.Equal(12, requiredFiles.Length);
        Assert.Contains(requiredFiles, path => path.EndsWith(
            "$(GhostShellNativeTerminalLibrary)",
            StringComparison.Ordinal));
        Assert.Contains(requiredFiles, path => path.EndsWith(
            "GHOSTTY-LICENSE",
            StringComparison.Ordinal));
        Assert.Contains(requiredFiles, path => path.EndsWith(
            "native-terminal-build-receipt.json",
            StringComparison.Ordinal));
        Assert.Contains(requiredFiles, path => path.EndsWith(
            "ghostty-vt-required-exports.txt",
            StringComparison.Ordinal));
        Assert.Contains(requiredFiles, path => path.EndsWith(
            "MANIFEST.sha256",
            StringComparison.Ordinal));
        Assert.Contains(requiredFiles, path => path.EndsWith(
            "zsh/ghostty-integration",
            StringComparison.Ordinal));
        Assert.Contains(
            "PrepareForBuild;Publish",
            (string?)validation.Attribute("BeforeTargets"),
            StringComparison.Ordinal);
        Assert.Contains(
            "!Exists('%(GhostShellNativeTerminalRequiredFile.Identity)')",
            (string?)error.Attribute("Condition"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Run ./scripts/build-libghostty-vt.sh --rid",
            (string?)error.Attribute("Text"),
            StringComparison.Ordinal);

        var projectText = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));
        Assert.DoesNotContain(
            "Exists('$(GhostShellNativeTerminalArtifactDirectory)",
            projectText,
            StringComparison.Ordinal);
        foreach (var rid in new[]
                 {
                     "osx-arm64",
                     "osx-x64",
                     "linux-arm64",
                     "linux-x64",
                     "win-x64",
                 })
        {
            Assert.Contains(rid, projectText, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(
        "EntryPoint\\s*=\\s*\"(?<entryPoint>[^\"]+)\"",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex EntryPointPattern();

    [GeneratedRegex(
        @"Marshal\.SizeOf<(?<type>GhosttyVt\w+)>\(\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex GenericMarshalSizePattern();

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
