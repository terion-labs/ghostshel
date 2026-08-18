namespace GhostShell.Terminal.Tests;

public sealed class ClaudeCodeExecutableResolverTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-claude-resolver-{Guid.NewGuid():N}");

    public ClaudeCodeExecutableResolverTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Resolver_skips_the_managed_shim_and_finds_the_real_executable()
    {
        var shimDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, "shim")).FullName;
        var realDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, "real")).FullName;
        WriteExecutable(Path.Combine(shimDirectory, CandidateName()));
        var real = WriteExecutable(Path.Combine(realDirectory, CandidateName()));
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = $"{shimDirectory}{Path.PathSeparator}{realDirectory}",
            [ClaudeCodeWrapperProcessHost.ShimDirectoryEnvironment] = shimDirectory,
        };

        var resolved = ClaudeCodeExecutableResolver.Resolve(environment, null);

        Assert.NotNull(resolved);
        Assert.Equal(
            ClaudeCodeExecutableResolver.Canonicalize(real),
            resolved.Path);
    }

    [Fact]
    public void Invalid_explicit_override_falls_back_to_path()
    {
        var realDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, "real")).FullName;
        var real = WriteExecutable(Path.Combine(realDirectory, CandidateName()));
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = realDirectory,
            [ClaudeCodeWrapperProcessHost.RealExecutableEnvironment] =
                Path.Combine(_temporaryDirectory, "missing"),
        };

        var resolved = ClaudeCodeExecutableResolver.Resolve(environment, null);

        Assert.Equal(
            ClaudeCodeExecutableResolver.Canonicalize(real),
            Assert.IsType<ClaudeCodeExecutable>(resolved).Path);
    }

    [Fact]
    public void Empty_path_entries_never_resolve_from_the_working_directory()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = $"{Path.PathSeparator}{Path.PathSeparator}",
        };

        Assert.Null(ClaudeCodeExecutableResolver.Resolve(environment, null));
    }

    [Fact]
    public void Non_executable_unix_candidate_does_not_shadow_a_later_real_claude()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var blockedDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, "blocked")).FullName;
        var realDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, "real-after-blocked")).FullName;
        File.WriteAllText(Path.Combine(blockedDirectory, "claude"), "not executable");
        var real = WriteExecutable(Path.Combine(realDirectory, "claude"));
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = $"{blockedDirectory}{Path.PathSeparator}{realDirectory}",
        };

        var resolved = ClaudeCodeExecutableResolver.Resolve(environment, null);

        Assert.Equal(
            ClaudeCodeExecutableResolver.Canonicalize(real),
            Assert.IsType<ClaudeCodeExecutable>(resolved).Path);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteExecutable(string path)
    {
        File.WriteAllText(path, "fixture");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string CandidateName() =>
        OperatingSystem.IsWindows() ? "claude.exe" : "claude";
}
