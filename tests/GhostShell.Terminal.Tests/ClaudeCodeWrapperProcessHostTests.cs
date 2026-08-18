using System.Diagnostics;

namespace GhostShell.Terminal.Tests;

public sealed class ClaudeCodeWrapperProcessHostTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-claude-wrapper-{Guid.NewGuid():N}");

    public ClaudeCodeWrapperProcessHostTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Documents_the_minimum_Claude_hook_protocol_version()
    {
        Assert.Equal(
            "2.1.145",
            ClaudeCodeWrapperProcessHost.MinimumNotificationVersion);
    }

    [Fact]
    public void Interactive_invocation_injects_one_plugin_and_preserves_user_arguments()
    {
        var fixture = CreateFixture();
        ProcessStartInfo? observed = null;

        var exitCode = ClaudeCodeWrapperProcessHost.Run(
            ["--settings", "{\"model\":\"opus\"}", "--plugin-dir", "/user/plugin", "work"],
            fixture.Environment,
            fixture.AppHost,
            startInfo =>
            {
                observed = startInfo;
                return 37;
            });

        Assert.Equal(37, exitCode);
        Assert.NotNull(observed);
        Assert.Equal(fixture.RealClaude, observed.FileName);
        Assert.Equal(
            [
                $"--plugin-dir={fixture.PluginDirectory}",
                "--settings",
                "{\"model\":\"opus\"}",
                "--plugin-dir",
                "/user/plugin",
                "work",
            ],
            observed.ArgumentList);
        Assert.DoesNotContain(
            fixture.ShimDirectory,
            observed.Environment["PATH"]!.Split(Path.PathSeparator));
        Assert.Equal("1", observed.Environment[ClaudeCodeWrapperProcessHost.DepthEnvironment]);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--print", "hello")]
    [InlineData("doctor")]
    public void Pass_through_invocations_do_not_receive_the_plugin(params string[] arguments)
    {
        var fixture = CreateFixture();
        ProcessStartInfo? observed = null;

        _ = ClaudeCodeWrapperProcessHost.Run(
            arguments,
            fixture.Environment,
            fixture.AppHost,
            startInfo =>
            {
                observed = startInfo;
                return 0;
            });

        Assert.Equal(arguments, Assert.IsType<ProcessStartInfo>(observed).ArgumentList);
    }

    [Fact]
    public void Exact_existing_plugin_path_is_not_duplicated()
    {
        var fixture = CreateFixture();
        ProcessStartInfo? observed = null;

        _ = ClaudeCodeWrapperProcessHost.Run(
            ["--plugin-dir", fixture.PluginDirectory, "work"],
            fixture.Environment,
            fixture.AppHost,
            startInfo =>
            {
                observed = startInfo;
                return 0;
            });

        Assert.Equal(
            ["--plugin-dir", fixture.PluginDirectory, "work"],
            Assert.IsType<ProcessStartInfo>(observed).ArgumentList);
    }

    [Fact]
    public void Recursion_guard_stops_before_starting_another_process()
    {
        var fixture = CreateFixture();
        fixture.Environment[ClaudeCodeWrapperProcessHost.DepthEnvironment] = "4";
        var started = false;

        var exitCode = ClaudeCodeWrapperProcessHost.Run(
            [],
            fixture.Environment,
            fixture.AppHost,
            _ =>
            {
                started = true;
                return 0;
            });

        Assert.Equal(126, exitCode);
        Assert.False(started);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Fixture CreateFixture()
    {
        var shimDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"), "shim")).FullName;
        var realDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"), "real")).FullName;
        var pluginDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"), "plugin")).FullName;
        var appHost = WriteFile(Path.Combine(_temporaryDirectory, "GhostShell"));
        _ = WriteFile(Path.Combine(shimDirectory, ClaudeName()));
        var realClaude = WriteFile(Path.Combine(realDirectory, ClaudeName()));
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = $"{shimDirectory}{Path.PathSeparator}{realDirectory}",
            [ClaudeCodeWrapperProcessHost.ShimDirectoryEnvironment] = shimDirectory,
            [ClaudeCodeWrapperProcessHost.PluginDirectoryEnvironment] = pluginDirectory,
        };
        return new Fixture(
            appHost,
            shimDirectory,
            realClaude,
            pluginDirectory,
            environment);
    }

    private string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string ClaudeName() =>
        OperatingSystem.IsWindows() ? "claude.exe" : "claude";

    private sealed record Fixture(
        string AppHost,
        string ShimDirectory,
        string RealClaude,
        string PluginDirectory,
        Dictionary<string, string?> Environment);
}
