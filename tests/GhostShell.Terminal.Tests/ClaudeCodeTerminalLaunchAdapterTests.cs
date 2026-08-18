using System.Diagnostics;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal.Tests;

public sealed class ClaudeCodeTerminalLaunchAdapterTests : IDisposable
{
    private readonly string _resources = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-claude-launch-{Guid.NewGuid():N}");

    public ClaudeCodeTerminalLaunchAdapterTests()
    {
        WriteResource("GhostShell");
        WriteResource("ghostshell-cli-shims/claude");
        WriteResource("claude-plugins/notifications/.claude-plugin/plugin.json");
        WriteResource("claude-plugins/notifications/hooks/hooks.json");
    }

    [Fact]
    public void Disabled_shell_integration_still_activates_the_local_zsh_shim()
    {
        var launch = Launch(
            "/bin/zsh",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/user/bin:/usr/bin" });
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.Disabled,
            null,
            "disabled");

        var prepared = Adapter().Prepare(preparation);

        Assert.NotSame(launch, prepared);
        var shimDirectory = Path.Combine(_resources, "ghostshell-cli-shims");
        Assert.Equal(
            shimDirectory,
            prepared.Environment["PATH"].Split(Path.PathSeparator)[0]);
        Assert.Equal(
            Path.Combine(_resources, "claude-plugins", "notifications"),
            prepared.Environment[ClaudeCodeWrapperProcessHost.PluginDirectoryEnvironment]);
        Assert.Equal(launch.Arguments, prepared.Arguments);
    }

    [Fact]
    public void Disabled_bash_rc_path_reorder_cannot_bypass_the_wrapper_function()
    {
        const string bash = "/bin/bash";
        if (!File.Exists(bash))
        {
            return;
        }

        var rcFile = WriteResource(
            "user-bash/.bashrc",
            "export PATH=/real-claude-bin:$PATH\n");
        var launch = Launch(
            bash,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin:/bin" },
            arguments: ["--noprofile", "--rcfile", rcFile, "-i", "-c", "type -t claude"]);
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.Disabled,
            null,
            "disabled");

        var prepared = Adapter().Prepare(preparation);
        var result = RunShell(prepared);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("function", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_zsh_rc_path_reorder_is_repaired_after_user_startup()
    {
        const string zsh = "/bin/zsh";
        if (!File.Exists(zsh))
        {
            return;
        }

        WriteResource(
            "terminal-shell-integration/zsh/.zshenv",
            File.ReadAllText(FindProductionZshBootstrap()));
        var userDotDirectory = Path.Combine(_resources, "user-zsh");
        WriteResource("user-zsh/.zshenv", "export GHOSTSHELL_TEST_ZSHENV=loaded\n");
        WriteResource("user-zsh/.zshrc", "export PATH=/real-claude-bin:$PATH\n");
        var launch = Launch(
            zsh,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = "/usr/bin:/bin",
                ["ZDOTDIR"] = userDotDirectory,
                ["PS1"] = string.Empty,
            },
            arguments: ["-i"]);
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.Disabled,
            null,
            "disabled");

        var prepared = Adapter().Prepare(preparation);
        var result = RunShell(
            prepared,
            "print -r -- $GHOSTSHELL_TEST_ZSHENV\nwhence -w claude\nexit\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("loaded", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("claude: function", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_fish_still_loads_the_post_startup_companion()
    {
        WriteResource(
            "terminal-shell-integration/fish/vendor_conf.d/ghostshell-claude.fish");
        var launch = Launch(
            "/usr/bin/fish",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin" });
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.Disabled,
            null,
            "disabled");

        var prepared = Adapter().Prepare(preparation);

        var integrationDirectory = Path.Combine(
            _resources,
            "terminal-shell-integration");
        Assert.Equal(
            integrationDirectory,
            prepared.Environment["XDG_DATA_DIRS"].Split(Path.PathSeparator)[0]);
        Assert.Contains(
            "/usr/share",
            prepared.Environment["XDG_DATA_DIRS"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_optional_post_rc_companion_keeps_the_path_injected_launch()
    {
        var launch = Launch(
            "/bin/zsh",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = "/usr/bin",
                ["ZDOTDIR"] = "/ghostty",
            });
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.Applied,
            TerminalShellIntegrationMode.Zsh,
            null);

        var prepared = Adapter().Prepare(preparation);

        Assert.StartsWith(
            Path.Combine(_resources, "ghostshell-cli-shims") + Path.PathSeparator,
            prepared.Environment["PATH"],
            StringComparison.Ordinal);
        Assert.Equal("/ghostty", prepared.Environment["ZDOTDIR"]);
    }

    [Fact]
    public void Connection_transport_process_is_never_activated()
    {
        var launch = Launch(
            "/usr/bin/ssh",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new TerminalConnectionMetadata("SSH: example.test:22", null));
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.IncompatibleLaunch,
            TerminalShellIntegrationMode.Zsh,
            null);

        Assert.Same(launch, Adapter().Prepare(preparation));
    }

    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("/bin/dash")]
    [InlineData("/usr/local/bin/ksh")]
    [InlineData("/usr/local/bin/nu")]
    [InlineData("/usr/local/bin/elvish")]
    public void Local_shell_without_a_companion_still_receives_the_plain_path_shim(
        string executable)
    {
        var launch = Launch(
            executable,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin" });
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.NotDetected,
            null,
            null);

        var prepared = Adapter().Prepare(preparation);

        Assert.StartsWith(
            Path.Combine(_resources, "ghostshell-cli-shims") + Path.PathSeparator,
            prepared.Environment["PATH"],
            StringComparison.Ordinal);
        Assert.Equal(launch.Arguments, prepared.Arguments);
    }

    [Theory]
    [InlineData("SSH: example.test:22")]
    [InlineData("Docker: default/api")]
    [InlineData("WSL: Ubuntu")]
    public void Remote_connection_boundaries_never_receive_the_host_shim(
        string boundary)
    {
        var launch = Launch(
            "/bin/sh",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new TerminalConnectionMetadata(boundary, null));
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.NotDetected,
            null,
            null);

        Assert.Same(launch, Adapter().Prepare(preparation));
    }

    [Fact]
    public void Missing_required_plugin_fails_open_without_partial_environment()
    {
        File.Delete(Path.Combine(
            _resources,
            "claude-plugins",
            "notifications",
            "hooks",
            "hooks.json"));
        var launch = Launch("/bin/zsh", new Dictionary<string, string>(StringComparer.Ordinal));
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.Disabled,
            null,
            null);

        Assert.Same(launch, Adapter().Prepare(preparation));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    public void Explicit_opt_out_leaves_the_launch_and_user_command_untouched(
        string value)
    {
        var launch = Launch(
            "/bin/zsh",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClaudeCodeWrapperProcessHost.DisableEnvironment] = value,
            });
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.Disabled,
            null,
            null);

        Assert.Same(launch, Adapter().Prepare(preparation));
    }

    [Fact]
    public void Windows_npm_batch_only_install_is_not_shadowed_by_the_managed_shim()
    {
        WriteResource("GhostShell.exe");
        WriteResource("ghostshell-cli-shims/claude.cmd");
        var npmDirectory = Path.Combine(_resources, "npm-bin");
        WriteResource("npm-bin/claude.cmd");
        var launch = Launch(
            "cmd.exe",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = npmDirectory });
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.NotDetected,
            null,
            null);

        var prepared = new ClaudeCodeTerminalLaunchAdapter(
            _resources,
            ClaudeCodeHostPlatform.Windows).Prepare(preparation);

        Assert.Same(launch, prepared);
    }

    [Fact]
    public void Windows_native_claude_install_activates_the_managed_shim()
    {
        WriteResource("GhostShell.exe");
        WriteResource("ghostshell-cli-shims/claude.cmd");
        var nativeDirectory = Path.Combine(_resources, "native-bin");
        WriteResource("native-bin/claude.exe");
        var launch = Launch(
            "cmd.exe",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = nativeDirectory });
        var preparation = new GhosttyShellIntegrationPreparation(
            launch,
            GhosttyShellIntegrationPreparationStatus.NotDetected,
            null,
            null);

        var prepared = new ClaudeCodeTerminalLaunchAdapter(
            _resources,
            ClaudeCodeHostPlatform.Windows).Prepare(preparation);

        Assert.NotSame(launch, prepared);
        Assert.Equal(
            Path.Combine(_resources, "claude-plugins", "notifications"),
            prepared.Environment[ClaudeCodeWrapperProcessHost.PluginDirectoryEnvironment]);
    }

    public void Dispose()
    {
        Directory.Delete(_resources, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ClaudeCodeTerminalLaunchAdapter Adapter() =>
        new(_resources, ClaudeCodeHostPlatform.Posix);

    private static TerminalLaunchRequest Launch(
        string executable,
        IReadOnlyDictionary<string, string> environment,
        TerminalConnectionMetadata? connectionMetadata = null,
        IReadOnlyList<string>? arguments = null) =>
        new(
            Environment.CurrentDirectory,
            executable,
            arguments ?? [],
            environment,
            new TerminalRenderProfileSnapshot(
                13,
                TerminalCursorStyle.Block,
                true,
                10_000,
                TerminalPalette.GhostShellDark),
            connectionMetadata: connectionMetadata);

    private string WriteResource(string relativePath, string content = "fixture")
    {
        var path = Path.Combine(
            _resources,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ShellResult RunShell(
        TerminalLaunchRequest launch,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
        }

        process.StandardInput.Close();
        if (!process.WaitForExit(5_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The shell startup acceptance fixture did not exit.");
        }

        return new ShellResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static string FindProductionZshBootstrap()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "GhostShell.Desktop",
                "Resources",
                "Claude",
                "terminal-shell-integration",
                "zsh",
                ".zshenv");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not locate the production GhostSHELL Zsh notification bootstrap.");
    }

    private sealed record ShellResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
