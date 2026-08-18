using System.Diagnostics;

namespace GhostShell.Terminal;

public static class ClaudeCodeWrapperProcessHost
{
    public const string CommandLineSwitch = "--ghostshell-claude-wrapper";
    internal const string MinimumNotificationVersion = "2.1.145";
    internal const string AppHostEnvironment = "GHOSTSHELL_CLAUDE_WRAPPER_HOST";
    internal const string ShimDirectoryEnvironment = "GHOSTSHELL_CLAUDE_SHIM_DIRECTORY";
    internal const string PluginDirectoryEnvironment = "GHOSTSHELL_CLAUDE_PLUGIN_DIRECTORY";
    internal const string RealExecutableEnvironment = "GHOSTSHELL_CLAUDE_REAL_PATH";
    internal const string DisableEnvironment = "GHOSTSHELL_CLAUDE_NOTIFICATIONS";
    internal const string DepthEnvironment = "GHOSTSHELL_CLAUDE_WRAPPER_DEPTH";
    internal const string VisitedTargetsEnvironment = "GHOSTSHELL_CLAUDE_WRAPPER_TARGETS";
    private const int MaximumWrapperDepth = 4;
    private const int CannotExecuteExitCode = 126;
    private const int CommandNotFoundExitCode = 127;

    public static int Run(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => entry.Value?.ToString(),
                StringComparer.Ordinal);
        return Run(arguments, environment, Environment.ProcessPath, StartAndWait);
    }

    internal static int Run(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        string? currentProcessPath,
        Func<ProcessStartInfo, int> startAndWait)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(startAndWait);

        var depth = ReadDepth(environment);
        if (depth >= MaximumWrapperDepth)
        {
            Console.Error.WriteLine("GhostSHELL stopped a recursive Claude launcher chain.");
            return CannotExecuteExitCode;
        }

        var executable = ClaudeCodeExecutableResolver.Resolve(environment, currentProcessPath);
        if (executable is null)
        {
            Console.Error.WriteLine("GhostSHELL could not find the real Claude executable.");
            return CommandNotFoundExitCode;
        }

        var childArguments = new List<string>(arguments.Count + 1);
        var pluginDirectory = Read(environment, PluginDirectoryEnvironment);
        if (!IsDisabled(environment)
            && pluginDirectory is not null
            && Directory.Exists(pluginDirectory)
            && ClaudeCodeInvocation.ShouldInjectPlugin(arguments, pluginDirectory))
        {
            // terminalSequence exists in Claude Code 2.1.141, while the
            // background-work fields needed for correct Stop semantics landed
            // in 2.1.145. Older CLIs remain runnable and simply ignore the
            // unsupported hook response; the minimum is an integration contract
            // rather than a per-launch version probe.
            // Claude declares --plugin-dir as variadic. The equals form keeps a
            // following positional prompt from being consumed as another path.
            childArguments.Add($"--plugin-dir={pluginDirectory}");
        }

        childArguments.AddRange(arguments);
        var startInfo = CreateStartInfo(executable, childArguments, environment);
        startInfo.Environment[DepthEnvironment] = (depth + 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[VisitedTargetsEnvironment] = AppendVisitedTarget(
            Read(environment, VisitedTargetsEnvironment),
            executable.Path);
        return startAndWait(startInfo);
    }

    private static ProcessStartInfo CreateStartInfo(
        ClaudeCodeExecutable executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.Path,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        var pathName = startInfo.Environment.Keys.FirstOrDefault(name =>
                string.Equals(name, "PATH", OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            ?? "PATH";
        startInfo.Environment[pathName] = ClaudeCodeExecutableResolver.RemoveDirectoryFromPath(
            startInfo.Environment.TryGetValue(pathName, out var path) ? path : null,
            Read(environment, ShimDirectoryEnvironment));
        return startInfo;
    }

    private static int StartAndWait(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine("GhostSHELL could not start the real Claude executable.");
                return CannotExecuteExitCode;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception error) when (error is InvalidOperationException
                                      or System.ComponentModel.Win32Exception
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("GhostSHELL could not start the real Claude executable.");
            return CannotExecuteExitCode;
        }
    }

    private static int ReadDepth(IReadOnlyDictionary<string, string?> environment) =>
        int.TryParse(
            Read(environment, DepthEnvironment),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var depth)
            && depth >= 0
                ? depth
                : 0;

    private static bool IsDisabled(IReadOnlyDictionary<string, string?> environment) =>
        string.Equals(Read(environment, DisableEnvironment), "0", StringComparison.Ordinal)
        || string.Equals(Read(environment, DisableEnvironment), "false", StringComparison.OrdinalIgnoreCase);

    private static string AppendVisitedTarget(string? current, string target)
    {
        var canonical = ClaudeCodeExecutableResolver.Canonicalize(target);
        return string.IsNullOrEmpty(current)
            ? canonical
            : $"{current}\n{canonical}";
    }

    private static string? Read(
        IReadOnlyDictionary<string, string?> environment,
        string name)
    {
        if (environment.TryGetValue(name, out var value))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return environment.FirstOrDefault(entry =>
                string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;
    }
}
