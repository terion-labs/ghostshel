namespace GhostShell.AccessibilityAcceptance.Tests;

public sealed class BoundaryProbeTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory(
        "ghostshell-accessibility-boundary-tests-").FullName;

    [Theory]
    [InlineData(":0", true)]
    [InlineData(":2.1", true)]
    [InlineData("unix:7.0", true)]
    [InlineData("localhost:10.0", false)]
    [InlineData("remote.example:0", false)]
    [InlineData("", false)]
    public void Linux_acceptance_requires_a_local_display(string display, bool expected)
    {
        Assert.Equal(expected, HostEnvironmentProbe.IsLocalDisplay(display));
    }

    [Theory]
    [InlineData(":99", "Xvfb", ":99", true)]
    [InlineData(":4.0", "Xwayland", ":4", true)]
    [InlineData("unix:7.0", "Xephyr", ":7", true)]
    [InlineData(":8", "Xnest", ":8", true)]
    [InlineData(":9", "nxagent", ":9", true)]
    [InlineData(":99", "Xorg", ":99", false)]
    [InlineData(":99", "Xvfb", ":98", false)]
    public void Virtual_x_server_must_own_the_active_display(
        string display,
        string processName,
        string processDisplay,
        bool expected)
    {
        Assert.Equal(
            expected,
            HostEnvironmentProbe.ProcessOwnsDisplay(
                display,
                processName,
                [processName, processDisplay, "-screen", "0"]));
    }

    [Fact]
    public void Screen_reader_paths_are_exact_and_platform_bounded()
    {
        Assert.True(ScreenReaderProbe.IsExpectedVoiceOverPath(
            "/System/Library/CoreServices/VoiceOver.app/Contents/MacOS/VoiceOver"));
        Assert.False(ScreenReaderProbe.IsExpectedVoiceOverPath("/tmp/VoiceOver"));
        Assert.False(ScreenReaderProbe.IsExpectedVoiceOverPath(
            "/tmp/VoiceOver.app/Contents/MacOS/VoiceOver"));
        Assert.True(ScreenReaderProbe.IsExpectedNarratorPath(
            "C:\\Windows\\System32\\Narrator.exe",
            "C:\\Windows"));
        Assert.False(ScreenReaderProbe.IsExpectedNarratorPath(
            "C:\\Users\\alice\\Narrator.exe",
            "C:\\Windows"));
    }

    [Theory]
    [InlineData("/usr/bin/python3", "/usr/bin/python3", "/usr/bin/orca")]
    [InlineData("/usr/bin/python3.13", "/usr/bin/python3", "/usr/bin/orca")]
    [InlineData("/usr/bin/python3.13", "orca", null)]
    public void Orca_identity_accepts_a_bound_system_interpreter_process(
        string liveExecutable,
        string firstArgument,
        string? secondArgument)
    {
        var arguments = secondArgument is null
            ? new[] { firstArgument }
            : new[] { firstArgument, secondArgument, "--replace" };

        Assert.Equal(
            "/usr/bin/orca",
            ScreenReaderProbe.ResolveExpectedOrcaLauncher(liveExecutable, arguments));
    }

    [Theory]
    [InlineData("/bin/sleep", "/bin/sleep", "/usr/bin/orca")]
    [InlineData("/usr/bin/python3", "/usr/bin/python3", "/home/alice/orca")]
    [InlineData("/usr/local/bin/python3", "orca", null)]
    [InlineData("/usr/bin/python3-malicious", "orca", null)]
    public void Orca_identity_rejects_unbound_or_decoy_arguments(
        string liveExecutable,
        string firstArgument,
        string? secondArgument)
    {
        var arguments = secondArgument is null
            ? new[] { firstArgument }
            : new[] { firstArgument, secondArgument };

        Assert.Null(ScreenReaderProbe.ResolveExpectedOrcaLauncher(liveExecutable, arguments));
    }

    [Theory]
    [InlineData("('unix:path=/run/user/1000/at-spi/bus_0,guid=abc',)", true)]
    [InlineData("('unix:path=/run/user/1000/session-bus',)", false)]
    [InlineData("AT_SPI_SESSION_BUS_PRESENT", false)]
    [InlineData("", false)]
    public void Orca_requires_a_queried_at_spi_bus_address(
        string response,
        bool expected)
    {
        Assert.Equal(expected, ScreenReaderProbe.IsAtSpiAddressResponse(response));
    }

    [Theory]
    [InlineData("Orca version 47.0, AT-SPI2 version: 2.54.1", "47.0")]
    [InlineData("Orca version 48.beta, AT-SPI2 version: 2.56.0, Session: x11", "48.beta")]
    public void Orca_version_parser_extracts_the_bounded_upstream_field(
        string output,
        string expected)
    {
        Assert.True(ScreenReaderProbe.TryParseOrcaVersion(output, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("Orca 47.0")]
    [InlineData("unrelated version 47.0")]
    [InlineData("")]
    public void Orca_version_parser_rejects_unrecognized_output(string output)
    {
        Assert.False(ScreenReaderProbe.TryParseOrcaVersion(output, out _));
    }

    [Fact]
    public void Captured_host_identity_uses_a_non_identifying_fingerprint()
    {
        var host = HostIdentity.Capture(
            OperatingSystem.IsMacOS()
                ? TargetPlatform.MacOS
                : OperatingSystem.IsWindows()
                    ? TargetPlatform.Windows
                    : TargetPlatform.LinuxX11,
            "a11y-lab-01",
            "operator-01");

        Assert.StartsWith("host-", host.HostFingerprint, StringComparison.Ordinal);
        Assert.Equal(21, host.HostFingerprint.Length);
        Assert.DoesNotContain(
            Environment.MachineName,
            host.HostFingerprint,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Linux_package_fingerprint_changes_with_any_package_file()
    {
        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable-v1");
        SetExecutable(executable);
        File.WriteAllText(Path.Combine(_temporaryDirectory, "support.dll"), "support-v1");

        var first = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");
        File.WriteAllText(Path.Combine(_temporaryDirectory, "support.dll"), "support-v2");
        var second = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");

        Assert.Equal("linux-x11-package", first.Build.PackageKind);
        Assert.Equal("GhostShell", first.Build.PackageExecutable);
        Assert.NotEqual(first.Build.PackageManifestSha256, second.Build.PackageManifestSha256);
        Assert.Equal(first.Build.ExecutableSha256, second.Build.ExecutableSha256);
    }

    [Fact]
    public void Package_fingerprint_is_stable_for_an_unchanged_tree()
    {
        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "empty-directory"));

        var first = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");
        var second = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");

        Assert.Equal(first.Build.PackageManifestSha256, second.Build.PackageManifestSha256);
    }

    [Fact]
    public void Package_fingerprint_changes_when_an_empty_directory_is_added()
    {
        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        var first = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");

        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "new-empty-directory"));
        var second = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");

        Assert.NotEqual(first.Build.PackageManifestSha256, second.Build.PackageManifestSha256);
    }

    [Fact]
    public void Unix_package_fingerprint_changes_when_file_permissions_change()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        var first = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");

        File.SetUnixFileMode(
            executable,
            File.GetUnixFileMode(executable)
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        var second = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1");

        Assert.NotEqual(first.Build.PackageManifestSha256, second.Build.PackageManifestSha256);
    }

    [Fact]
    public async Task Unix_package_rejects_a_fifo_without_blocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        var fifo = Path.Combine(_temporaryDirectory, "fingerprint-blocker");
        CreateFifo(fifo);

        var inspection = Task.Run(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Unix_package_rejects_a_primary_executable_fifo_without_blocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        CreateFifo(executable);

        var inspection = Task.Run(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(inspection.IsCompleted);
    }

    [Fact]
    public async Task Package_rejects_an_oversized_sparse_file_before_reading_content()
    {
        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        var oversized = Path.Combine(_temporaryDirectory, "oversized.sparse");
        using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength((8L * 1024 * 1024 * 1024) + 1);
        }

        var inspection = Task.Run(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-1"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Linux_package_rejects_wrong_executable_name_casing_before_launch()
    {
        var executable = Path.Combine(_temporaryDirectory, "ghostshell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);

        Assert.Throws<FileNotFoundException>(() => PackageFingerprint.Inspect(
            executable,
            TargetPlatform.LinuxX11,
            "rc-1"));
    }

    [Fact]
    public void Package_inspection_options_require_exact_bounded_identity_arguments()
    {
        var options = PackageInspectOptions.Parse(
        [
            "--platform", "MacOS",
            "--build-label", "macos-1.2.3-42",
            "--package", "/packages/GhostShell.app",
        ]);

        Assert.Equal(TargetPlatform.MacOS, options.Platform);
        Assert.Equal("macos-1.2.3-42", options.BuildLabel);
        Assert.Throws<UsageException>(() => PackageInspectOptions.Parse(
        [
            "--platform", "MacOS",
            "--build-label", "macos-1.2.3-42",
            "--build-label", "duplicate",
        ]));
    }

    [Fact]
    public void Mac_package_publication_options_require_exact_bounded_arguments()
    {
        var options = MacOsPackagePublishOptions.Parse(
        [
            "--build-label", "macos-1.2.3-42",
            "--package", "/private/GhostShell.app",
            "--output", "/packages/GhostShell.app",
        ]);

        Assert.Equal("macos-1.2.3-42", options.BuildLabel);
        Assert.Equal("/private/GhostShell.app", options.PackagePath);
        Assert.Equal("/packages/GhostShell.app", options.DestinationPath);
        Assert.Throws<UsageException>(() => MacOsPackagePublishOptions.Parse(
        [
            "--build-label", "macos-1.2.3-42",
            "--package", "/private/GhostShell.app",
            "--package", "/private/replacement/GhostShell.app",
        ]));
    }

    [Fact]
    public void Mac_package_requires_exact_bundle_identity()
    {
        var bundle = Path.Combine(_temporaryDirectory, "GhostShell.app");
        var macOS = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOS);
        var executable = Path.Combine(macOS, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        WriteInfoPlist(bundle, "app.ghostshell");

        var inspection = PackageFingerprint.Inspect(
            bundle,
            TargetPlatform.MacOS,
            "rc-1");

        Assert.Equal("macos-application-bundle", inspection.Build.PackageKind);
        Assert.Equal("app.ghostshell", inspection.Build.ApplicationIdentity);
    }

    [Fact]
    public void Mac_package_accepts_a_binary_property_list()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var bundle = CreateMacBundle();
        WriteInfoPlist(bundle, "app.ghostshell");
        var infoPlist = Path.Combine(bundle, "Contents", "Info.plist");
        using var plutil = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/usr/bin/plutil",
            UseShellExecute = false,
            ArgumentList = { "-convert", "binary1", infoPlist },
        });
        Assert.NotNull(plutil);
        Assert.True(plutil.WaitForExit(milliseconds: 2_000));
        Assert.Equal(0, plutil.ExitCode);

        var inspection = PackageFingerprint.Inspect(
            bundle,
            TargetPlatform.MacOS,
            "rc-1");

        Assert.Equal("app.ghostshell", inspection.Build.ApplicationIdentity);
    }

    [Fact]
    public async Task Mac_package_rejects_an_info_plist_fifo_without_blocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var bundle = CreateMacBundle();
        CreateFifo(Path.Combine(bundle, "Contents", "Info.plist"));

        var inspection = Task.Run(() => PackageFingerprint.Inspect(
            bundle,
            TargetPlatform.MacOS,
            "rc-1"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Mac_package_rejects_wrong_bundle_identifier()
    {
        var bundle = Path.Combine(_temporaryDirectory, "GhostShell.app");
        var macOS = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOS);
        var executable = Path.Combine(macOS, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        WriteInfoPlist(bundle, "example.invalid");

        Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
            bundle,
            TargetPlatform.MacOS,
            "rc-1"));
    }

    [Fact]
    public void Mac_package_rejects_duplicate_keys_even_with_mixed_value_types()
    {
        var bundle = CreateMacBundle();
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>CFBundleIdentifier</key><string>app.ghostshell</string>
              <key>CFBundleIdentifier</key><true/>
              <key>CFBundleExecutable</key><string>GhostShell</string>
            </dict></plist>
            """);

        Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
            bundle,
            TargetPlatform.MacOS,
            "rc-1"));
    }

    [Fact]
    public void Mac_package_rejects_a_non_plist_document_root()
    {
        var bundle = CreateMacBundle();
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <root><dict>
              <key>CFBundleIdentifier</key><string>app.ghostshell</string>
              <key>CFBundleExecutable</key><string>GhostShell</string>
            </dict></root>
            """);

        Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
            bundle,
            TargetPlatform.MacOS,
            "rc-1"));
    }

    [Fact]
    public void Package_rejects_symbolic_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        var external = Path.Combine(Path.GetTempPath(), $"ghostshell-a11y-{Guid.NewGuid():N}");
        File.WriteAllText(external, "external");
        var link = Path.Combine(_temporaryDirectory, "linked-file");
        File.CreateSymbolicLink(link, external);
        try
        {
            Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
                _temporaryDirectory,
                TargetPlatform.LinuxX11,
                "rc-1"));
        }
        finally
        {
            File.Delete(external);
        }
    }

    [Fact]
    public void Package_rejects_directory_links_before_recursive_fingerprinting()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var package = Path.Combine(_temporaryDirectory, "package");
        Directory.CreateDirectory(package);
        var executable = Path.Combine(package, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        var external = Path.Combine(_temporaryDirectory, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "outside-package"), "external");
        Directory.CreateSymbolicLink(Path.Combine(package, "linked-directory"), external);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PackageFingerprint.Inspect(package, TargetPlatform.LinuxX11, "rc-1"));

        Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_rejects_a_linked_package_root_before_fingerprinting()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var package = Path.Combine(_temporaryDirectory, "real-package");
        Directory.CreateDirectory(package);
        var executable = Path.Combine(package, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        var packageAlias = Path.Combine(_temporaryDirectory, "package-alias");
        Directory.CreateSymbolicLink(packageAlias, package);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PackageFingerprint.Inspect(packageAlias, TargetPlatform.LinuxX11, "rc-1"));

        Assert.Contains("root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_output_must_not_resolve_inside_the_package()
    {
        var package = Path.Combine(_temporaryDirectory, "package-boundary");
        var sibling = Path.Combine(_temporaryDirectory, "evidence-sibling");
        Directory.CreateDirectory(package);

        Assert.True(EvidenceFiles.IsSameOrDescendantPath(
            Path.Combine(package, "evidence"),
            package));
        Assert.True(EvidenceFiles.IsSameOrDescendantPath(package, package));
        Assert.False(EvidenceFiles.IsSameOrDescendantPath(sibling, package));

        if (!OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(_temporaryDirectory, "package-alias");
            Directory.CreateSymbolicLink(alias, package);
            Assert.True(EvidenceFiles.IsSameOrDescendantPath(
                Path.Combine(alias, "evidence"),
                package));
        }
    }

    [Fact]
    public void Mac_evidence_boundary_rejects_case_and_unicode_aliases_inside_package()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var composedPackage = Path.Combine(_temporaryDirectory, "Caf\u00e9", "GhostShell.app");
        Directory.CreateDirectory(composedPackage);
        var decomposedCandidate = Path.Combine(
            _temporaryDirectory,
            "Cafe\u0301",
            "ghostshell.app",
            "evidence");

        Assert.True(EvidenceFiles.IsSameOrDescendantPath(
            decomposedCandidate,
            composedPackage));
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private static void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void CreateFifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/usr/bin/mkfifo",
            UseShellExecute = false,
            ArgumentList = { path },
        });
        Assert.NotNull(mkfifo);
        Assert.True(mkfifo.WaitForExit(milliseconds: 2_000));
        Assert.Equal(0, mkfifo.ExitCode);
    }

    private string CreateMacBundle()
    {
        var bundle = Path.Combine(_temporaryDirectory, $"{Guid.NewGuid():N}", "GhostShell.app");
        var macOS = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOS);
        var executable = Path.Combine(macOS, "GhostShell");
        File.WriteAllText(executable, "executable");
        SetExecutable(executable);
        return bundle;
    }

    private static void WriteInfoPlist(string bundle, string identifier)
    {
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Info.plist"),
            $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleIdentifier</key><string>{{identifier}}</string>
              <key>CFBundleExecutable</key><string>GhostShell</string>
              <key>CFBundleShortVersionString</key><string>1.0.0</string>
            </dict></plist>
            """);
    }
}
