namespace GhostShell.Terminal;

internal sealed record ClaudeCodeExecutable(string Path);

internal static class ClaudeCodeExecutableResolver
{
    public static ClaudeCodeExecutable? Resolve(
        IReadOnlyDictionary<string, string?> environment,
        string? currentProcessPath)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var shimDirectory = Read(environment, ClaudeCodeWrapperProcessHost.ShimDirectoryEnvironment);
        var visited = ReadVisitedTargets(environment);
        var explicitPath = Read(environment, ClaudeCodeWrapperProcessHost.RealExecutableEnvironment);
        if (explicitPath is not null
            && Path.IsPathFullyQualified(explicitPath)
            && TryCandidate(
                explicitPath,
                shimDirectory,
                currentProcessPath,
                visited,
                out var resolved))
        {
            return resolved;
        }

        var path = Read(environment, "PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)
                || PathsEqual(entry, shimDirectory))
            {
                continue;
            }

            foreach (var fileName in CandidateFileNames())
            {
                if (TryCandidate(
                        Path.Combine(entry, fileName),
                        shimDirectory,
                        currentProcessPath,
                        visited,
                        out resolved))
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    public static string RemoveDirectoryFromPath(string? path, string? directory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(directory))
        {
            return path ?? string.Empty;
        }

        return string.Join(
            Path.PathSeparator,
            path.Split(Path.PathSeparator)
                .Where(entry => !PathsEqual(entry, directory)));
    }

    public static string Canonicalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName
                ?? fullPath;
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            return fullPath;
        }
    }

    private static bool TryCandidate(
        string? candidate,
        string? shimDirectory,
        string? currentProcessPath,
        IReadOnlySet<string> visited,
        out ClaudeCodeExecutable? executable)
    {
        executable = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string canonical;
        try
        {
            canonical = Canonicalize(candidate);
        }
        catch (Exception error) when (error is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }

        if (!File.Exists(canonical)
            || !IsExecutable(canonical)
            || IsInDirectory(canonical, shimDirectory)
            || PathsEqual(canonical, currentProcessPath)
            || visited.Contains(canonical))
        {
            return false;
        }

        var extension = Path.GetExtension(canonical);
        if (OperatingSystem.IsWindows()
            && (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)))
        {
            // Re-forwarding already parsed argv through cmd.exe necessarily
            // reinterprets percent expansion and quoting. Current Claude Code
            // ships a native executable; skip legacy npm batch launchers rather
            // than silently changing a user's prompt or settings JSON.
            return false;
        }

        executable = new ClaudeCodeExecutable(canonical);
        return true;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & executeBits) != UnixFileMode.None;
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static IEnumerable<string> CandidateFileNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return "claude";
            yield break;
        }

        yield return "claude.exe";
        yield return "claude.com";
    }

    private static HashSet<string> ReadVisitedTargets(
        IReadOnlyDictionary<string, string?> environment)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var result = new HashSet<string>(comparer);
        var value = Read(environment, ClaudeCodeWrapperProcessHost.VisitedTargetsEnvironment);
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        foreach (var target in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                result.Add(Canonicalize(target));
            }
            catch (Exception error) when (error is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
            {
                // Ignore malformed inherited guard entries.
            }
        }

        return result;
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

    private static bool IsInDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return PathsEqual(Path.GetDirectoryName(path), Canonicalize(directory));
        }
        catch (Exception error) when (error is ArgumentException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Canonicalize(left),
                Canonicalize(right),
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
