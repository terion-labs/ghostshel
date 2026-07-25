using System.Text.Json;

namespace GhostShell.TerminalAcceptance.Tests;

public sealed class PackageFingerprintTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory(
        "ghostshell-terminal-acceptance-tests-").FullName;

    [Fact]
    public void Fingerprint_identifies_backend_and_hashes_the_complete_package()
    {
        var executable = Path.Combine(_temporaryDirectory, "GhostShell");
        File.WriteAllText(executable, "executable-v1");
        File.WriteAllText(Path.Combine(_temporaryDirectory, "support.dll"), "support-v1");
        WriteDependencyManifest();

        var first = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1");

        Assert.Equal("GhostShell", first.Build.PackageExecutable);
        Assert.Equal(3, first.Build.PackageFileCount);
        Assert.Equal(64, first.Build.ExecutableSha256.Length);
        Assert.Contains("XTerm.NET 1.0.15", first.Backend.Renderer, StringComparison.Ordinal);
        Assert.Contains("Porta.Pty 1.0.7", first.Backend.PtyAdapter, StringComparison.Ordinal);
        Assert.Contains("Linux Unix PTY", first.Backend.PtySubstrate, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_temporaryDirectory, "support.dll"), "support-v2");
        var second = PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1");

        Assert.NotEqual(first.Build.PackageManifestSha256, second.Build.PackageManifestSha256);
        Assert.Equal(first.Build.ExecutableSha256, second.Build.ExecutableSha256);
    }

    [Fact]
    public void Fingerprint_fails_when_backend_dependencies_cannot_be_proven()
    {
        File.WriteAllText(Path.Combine(_temporaryDirectory, "GhostShell"), "executable");
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "GhostShell.deps.json"),
            "{\"libraries\":{}}\n");

        var exception = Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1"));

        Assert.Contains("Porta.Pty", exception.Message, StringComparison.Ordinal);
        Assert.Contains("XTerm.NET", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_rejects_ambiguous_backend_versions()
    {
        File.WriteAllText(Path.Combine(_temporaryDirectory, "GhostShell"), "executable");
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "GhostShell.deps.json"),
            "{\"libraries\":{\"Porta.Pty/1.0.7\":{},\"Porta.Pty/2.0.0\":{},\"XTerm.NET/1.0.15\":{}}}\n");

        var exception = Assert.Throws<InvalidDataException>(() => PackageFingerprint.Inspect(
            _temporaryDirectory,
            TargetPlatform.LinuxX11,
            "rc-20260723-1"));

        Assert.Contains("more than one Porta.Pty", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private void WriteDependencyManifest()
    {
        var manifest = new
        {
            libraries = new Dictionary<string, object>
            {
                ["GhostShell/1.0.0"] = new { },
                ["Porta.Pty/1.0.7"] = new { },
                ["XTerm.NET/1.0.15"] = new { },
            },
        };
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "GhostShell.deps.json"),
            JsonSerializer.Serialize(manifest));
    }
}
