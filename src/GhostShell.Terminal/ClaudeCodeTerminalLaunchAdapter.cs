using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

internal enum ClaudeCodeHostPlatform
{
    Posix,
    Windows,
}

internal sealed class ClaudeCodeTerminalLaunchAdapter
{
    private const string BashClaudeFunctionEnvironment = "BASH_FUNC_claude%%";
    private enum SupportedShell
    {
        Bash,
        Zsh,
        Fish,
        Cmd,
        PowerShell,
    }

    private const string GhosttyBashBootstrapEnvironment =
        "GHOSTSHELL_GHOSTTY_BASH_BOOTSTRAP";
    private const string GhosttyZshBootstrapEnvironment =
        "GHOSTSHELL_GHOSTTY_ZSH_BOOTSTRAP";
    private const string OriginalZshDotDirectoryEnvironment =
        "GHOSTSHELL_CLAUDE_ZSH_ZDOTDIR";
    private const string OriginalZshDotDirectoryPresentEnvironment =
        "GHOSTSHELL_CLAUDE_ZSH_ZDOTDIR_PRESENT";
    private readonly string _resourceDirectory;
    private readonly ClaudeCodeHostPlatform _platform;

    public ClaudeCodeTerminalLaunchAdapter()
        : this(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? ClaudeCodeHostPlatform.Windows
                : ClaudeCodeHostPlatform.Posix)
    {
    }

    internal ClaudeCodeTerminalLaunchAdapter(
        string resourceDirectory,
        ClaudeCodeHostPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDirectory);
        _resourceDirectory = Path.GetFullPath(resourceDirectory);
        _platform = platform;
    }

    public TerminalLaunchRequest Prepare(GhosttyShellIntegrationPreparation shellPreparation)
    {
        ArgumentNullException.ThrowIfNull(shellPreparation);
        var launch = shellPreparation.Launch;
        if (NotificationsDisabled(launch.Environment))
        {
            return launch;
        }

        if (!IsLocalLaunch(launch))
        {
            return launch;
        }

        var appHost = Path.Combine(
            _resourceDirectory,
            _platform == ClaudeCodeHostPlatform.Windows
                ? "GhostShell.exe"
                : "GhostShell");
        var shimDirectory = Path.Combine(_resourceDirectory, "ghostshell-cli-shims");
        var shim = Path.Combine(
            shimDirectory,
            _platform == ClaudeCodeHostPlatform.Windows ? "claude.cmd" : "claude");
        var pluginDirectory = Path.Combine(
            _resourceDirectory,
            "claude-plugins",
            "notifications");
        if (!File.Exists(appHost)
            || !File.Exists(shim)
            || !File.Exists(Path.Combine(pluginDirectory, ".claude-plugin", "plugin.json"))
            || !File.Exists(Path.Combine(pluginDirectory, "hooks", "hooks.json")))
        {
            return launch;
        }

        if (_platform == ClaudeCodeHostPlatform.Windows
            && !HasCompatibleWindowsClaude(launch.Environment, shimDirectory))
        {
            // The supported npm fallback exposes only claude.cmd/.ps1. Do not
            // shadow it when no native Claude target is available: preserving
            // the user's working command is more important than interception.
            return launch;
        }

        var environment = launch.Environment.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
        environment[ClaudeCodeWrapperProcessHost.AppHostEnvironment] = appHost;
        environment[ClaudeCodeWrapperProcessHost.ShimDirectoryEnvironment] = shimDirectory;
        environment[ClaudeCodeWrapperProcessHost.PluginDirectoryEnvironment] = pluginDirectory;
        PreserveDevelopmentDotnetRoot(environment);
        PrependPath(environment, shimDirectory);

        var arguments = launch.Arguments.ToArray();
        var shell = DetectSupportedShell(shellPreparation);
        switch (shell)
        {
            case SupportedShell.Bash:
                PrepareBashFunction(environment);
                if (shellPreparation.IsApplied)
                {
                    _ = TryPrepareBash(environment);
                }

                break;
            case SupportedShell.Zsh:
                _ = TryPrepareZsh(environment, shellPreparation.IsApplied);
                break;
            case SupportedShell.Fish:
                _ = TryPrepareFish(environment);
                break;
            case SupportedShell.Cmd or SupportedShell.PowerShell:
                arguments = PrepareWindowsArguments(shell.Value, arguments);
                break;
            case null:
                break;
        }

        return Copy(launch, arguments, environment);
    }

    private SupportedShell? DetectSupportedShell(
        GhosttyShellIntegrationPreparation preparation)
    {
        if (_platform == ClaudeCodeHostPlatform.Posix)
        {
            if (preparation.IsApplied)
            {
                return preparation.Shell switch
                {
                    TerminalShellIntegrationMode.Bash => SupportedShell.Bash,
                    TerminalShellIntegrationMode.Fish => SupportedShell.Fish,
                    TerminalShellIntegrationMode.Zsh => SupportedShell.Zsh,
                    _ => null,
                };
            }

            var posixExecutable = preparation.Launch.Executable
                ?? Environment.GetEnvironmentVariable("SHELL")
                ?? string.Empty;
            return Path.GetFileName(posixExecutable).ToLowerInvariant() switch
            {
                "bash" => SupportedShell.Bash,
                "fish" => SupportedShell.Fish,
                "zsh" => SupportedShell.Zsh,
                _ => null,
            };
        }

        var windowsExecutable = preparation.Launch.Executable
            ?? Environment.GetEnvironmentVariable("COMSPEC")
            ?? "cmd.exe";
        return Path.GetFileName(windowsExecutable).ToLowerInvariant() switch
        {
            "cmd" or "cmd.exe" => SupportedShell.Cmd,
            "powershell" or "powershell.exe" or "pwsh" or "pwsh.exe" =>
                SupportedShell.PowerShell,
            _ => null,
        };
    }

    private static bool IsLocalLaunch(TerminalLaunchRequest launch)
    {
        var boundary = launch.ConnectionMetadata?.ConnectionBoundary;
        if (boundary is null)
        {
            return true;
        }

        return boundary.StartsWith("Local:", StringComparison.Ordinal);
    }

    private static bool HasCompatibleWindowsClaude(
        IReadOnlyDictionary<string, string> environment,
        string shimDirectory)
    {
        var explicitPath = ReadEnvironment(
            environment,
            ClaudeCodeWrapperProcessHost.RealExecutableEnvironment);
        if (IsNativeWindowsClaude(explicitPath, shimDirectory))
        {
            return true;
        }

        var path = ReadEnvironment(environment, "PATH")
            ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var entry in path.Split(';'))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0
                || PathsEqual(directory, shimDirectory))
            {
                continue;
            }

            if (IsNativeWindowsClaude(Path.Combine(directory, "claude.exe"), shimDirectory)
                || IsNativeWindowsClaude(
                    Path.Combine(directory, "claude.com"),
                    shimDirectory))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNativeWindowsClaude(string? path, string shimDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !File.Exists(path)
            || PathsEqual(Path.GetDirectoryName(path), shimDirectory))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadEnvironment(
        IReadOnlyDictionary<string, string> environment,
        string name) =>
        environment.FirstOrDefault(entry =>
                string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }
    }

    private bool TryPrepareBash(IDictionary<string, string> environment)
    {
        var bootstrap = Path.Combine(
            _resourceDirectory,
            "terminal-shell-integration",
            "bash",
            "ghostshell-claude.bash");
        if (!File.Exists(bootstrap)
            || !environment.TryGetValue("ENV", out var ghosttyBootstrap)
            || !File.Exists(ghosttyBootstrap))
        {
            return false;
        }

        environment[GhosttyBashBootstrapEnvironment] = ghosttyBootstrap;
        environment["ENV"] = bootstrap;
        return true;
    }

    private static void PrepareBashFunction(IDictionary<string, string> environment)
    {
        // Bash imports exported functions before it reads startup files. Keep
        // interception independent of PATH so a normal .bashrc prepend cannot
        // put the real Claude ahead of the managed shim when Ghostty shell
        // integration is disabled. The explicit notification opt-out returns
        // before this environment is created.
        environment[BashClaudeFunctionEnvironment] =
            "() { \"$GHOSTSHELL_CLAUDE_WRAPPER_HOST\" "
            + "--ghostshell-claude-wrapper \"$@\"; }";
    }

    private bool TryPrepareZsh(
        IDictionary<string, string> environment,
        bool ghosttyIntegrationApplied)
    {
        var integrationDirectory = Path.Combine(
            _resourceDirectory,
            "terminal-shell-integration",
            "zsh");
        var bootstrap = Path.Combine(integrationDirectory, ".zshenv");
        if (!File.Exists(bootstrap))
        {
            return false;
        }

        if (ghosttyIntegrationApplied)
        {
            if (!environment.TryGetValue("ZDOTDIR", out var ghosttyDirectory))
            {
                return false;
            }

            var ghosttyBootstrap = Path.Combine(ghosttyDirectory, ".zshenv");
            if (!File.Exists(ghosttyBootstrap))
            {
                return false;
            }

            environment[GhosttyZshBootstrapEnvironment] = ghosttyBootstrap;
        }
        else
        {
            var original = environment.TryGetValue("ZDOTDIR", out var configured)
                ? configured
                : Environment.GetEnvironmentVariable("ZDOTDIR");
            environment[OriginalZshDotDirectoryPresentEnvironment] =
                original is null ? "0" : "1";
            if (original is not null)
            {
                environment[OriginalZshDotDirectoryEnvironment] = original;
            }
        }

        environment["ZDOTDIR"] = integrationDirectory;
        return true;
    }

    private bool TryPrepareFish(IDictionary<string, string> environment)
    {
        var integrationDirectory = Path.Combine(
            _resourceDirectory,
            "terminal-shell-integration");
        if (!File.Exists(Path.Combine(
                integrationDirectory,
                "fish",
                "vendor_conf.d",
                "ghostshell-claude.fish")))
        {
            return false;
        }

        var current = environment.TryGetValue("XDG_DATA_DIRS", out var configured)
            ? configured
            : Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        current = string.IsNullOrEmpty(current)
            ? "/usr/local/share:/usr/share"
            : current;
        var withoutIntegration = ClaudeCodeExecutableResolver.RemoveDirectoryFromPath(
            current,
            integrationDirectory);
        environment["XDG_DATA_DIRS"] = string.IsNullOrEmpty(withoutIntegration)
            ? integrationDirectory
            : $"{integrationDirectory}{Path.PathSeparator}{withoutIntegration}";
        return true;
    }

    private string[] PrepareWindowsArguments(
        SupportedShell shell,
        IReadOnlyList<string> arguments)
    {
        if (shell == SupportedShell.Cmd)
        {
            if (arguments.Any(argument =>
                    string.Equals(argument, "/c", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(argument, "/k", StringComparison.OrdinalIgnoreCase)))
            {
                return [.. arguments];
            }

            var bootstrap = Path.Combine(
                _resourceDirectory,
                "terminal-shell-integration",
                "windows",
                "ghostshell-claude.cmd");
            return File.Exists(bootstrap)
                ? [.. arguments, "/K", bootstrap]
                : [.. arguments];
        }

        if (arguments.Any(argument => argument is "-Command" or "-File" or "-EncodedCommand"
            || string.Equals(argument, "-c", StringComparison.OrdinalIgnoreCase)))
        {
            return [.. arguments];
        }

        var powershellBootstrap = Path.Combine(
            _resourceDirectory,
            "terminal-shell-integration",
            "windows",
            "ghostshell-claude.ps1");
        return File.Exists(powershellBootstrap)
            ? [.. arguments, "-NoExit", "-File", powershellBootstrap]
            : [.. arguments];
    }

    private static void PrependPath(
        IDictionary<string, string> environment,
        string shimDirectory)
    {
        var pathName = environment.Keys.FirstOrDefault(name =>
                string.Equals(name, "PATH", OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            ?? "PATH";
        environment.TryGetValue(pathName, out var configuredPath);
        var current = configuredPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var withoutShim = ClaudeCodeExecutableResolver.RemoveDirectoryFromPath(
            current,
            shimDirectory);
        environment[pathName] = string.IsNullOrEmpty(withoutShim)
            ? shimDirectory
            : $"{shimDirectory}{Path.PathSeparator}{withoutShim}";
    }

    private static bool NotificationsDisabled(
        IReadOnlyDictionary<string, string> environment)
    {
        var configured = environment.FirstOrDefault(entry =>
                string.Equals(
                    entry.Key,
                    ClaudeCodeWrapperProcessHost.DisableEnvironment,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            .Value;
        var value = configured
            ?? Environment.GetEnvironmentVariable(
                ClaudeCodeWrapperProcessHost.DisableEnvironment);
        return string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static void PreserveDevelopmentDotnetRoot(
        IDictionary<string, string> environment)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || !string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dotnetRoot = Path.GetDirectoryName(processPath);
        if (!string.IsNullOrWhiteSpace(dotnetRoot)
            && !environment.ContainsKey("DOTNET_ROOT"))
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
        }
    }

    private static TerminalLaunchRequest Copy(
        TerminalLaunchRequest launch,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment) =>
        new(
            launch.WorkingDirectory,
            launch.Executable,
            arguments,
            environment,
            launch.RenderProfile,
            launch.Keymap,
            launch.ConnectionId,
            launch.ConnectionMetadata,
            launch.InitialCommand,
            launch.ShellActivityFallback,
            launch.MultiplexerSession);
}
