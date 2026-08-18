namespace GhostShell.Terminal;

internal static class ClaudeCodeInvocation
{
    private static readonly HashSet<string> ManagementCommands = new(
        [
            "agents",
            "auth",
            "api-key",
            "config",
            "daemon",
            "doctor",
            "install",
            "mcp",
            "plugin",
            "plugins",
            "project",
            "rc",
            "remote-control",
            "setup-token",
            "update",
            "upgrade",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> OptionsWithOneValue = new(
        [
            "--output-format",
            "--json-schema",
            "--input-format",
            "--max-budget-usd",
            "--system-prompt",
            "--append-system-prompt",
            "--permission-mode",
            "--model",
            "--agent",
            "--fallback-model",
            "--settings",
            "--session-id",
            "--agents",
            "--setting-sources",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> OptionsWithOptionalValue = new(
        [
            "-d",
            "--debug",
            "-r",
            "--resume",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> OptionsWithManyValues = new(
        [
            "--allowedTools",
            "--allowed-tools",
            "--tools",
            "--disallowedTools",
            "--disallowed-tools",
            "--mcp-config",
            "--add-dir",
            "--betas",
            "--plugin-dir",
        ],
        StringComparer.Ordinal);

    public static bool ShouldInjectPlugin(
        IReadOnlyList<string> arguments,
        string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        if (ContainsPassThroughOption(arguments)
            || IsManagementCommand(arguments)
            || ContainsPluginDirectory(arguments, pluginDirectory))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsPassThroughOption(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                return false;
            }

            if (argument is "-h" or "--help" or "-v" or "--version"
                or "-p" or "--print" or "--safe-mode")
            {
                return true;
            }

            if (argument.StartsWith("--print=", StringComparison.Ordinal)
                || argument.StartsWith("--safe-mode=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsManagementCommand(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                return false;
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal)
                || string.Equals(argument, "-", StringComparison.Ordinal))
            {
                return ManagementCommands.Contains(argument);
            }

            if (argument.Contains('='))
            {
                continue;
            }

            if (OptionsWithOneValue.Contains(argument))
            {
                index++;
                continue;
            }

            if (OptionsWithOptionalValue.Contains(argument))
            {
                if (index + 1 < arguments.Count
                    && !arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    index++;
                }

                continue;
            }

            if (OptionsWithManyValues.Contains(argument))
            {
                // Variadic option values make later positional tokens ambiguous.
                // Conservatively keep the plugin enabled instead of treating a
                // path or tool name as a management subcommand.
                return false;
            }
        }

        return false;
    }

    private static bool ContainsPluginDirectory(
        IReadOnlyList<string> arguments,
        string pluginDirectory)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                return false;
            }

            if (argument.StartsWith("--plugin-dir=", StringComparison.Ordinal))
            {
                if (PathsEqual(
                        argument["--plugin-dir=".Length..],
                        pluginDirectory))
                {
                    return true;
                }

                continue;
            }

            if (!string.Equals(argument, "--plugin-dir", StringComparison.Ordinal))
            {
                continue;
            }

            while (++index < arguments.Count
                   && !arguments[index].StartsWith("-", StringComparison.Ordinal))
            {
                if (PathsEqual(arguments[index], pluginDirectory))
                {
                    return true;
                }
            }

            index--;
        }

        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }
    }
}
