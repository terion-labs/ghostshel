namespace GhostShell.Infrastructure;

/// <summary>
/// Resolves executables to absolute paths before a launch plan leaves Infrastructure.
/// </summary>
public sealed class PathConnectionExecutableLocator : IConnectionExecutableLocator
{
    public string? Find(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0'))
        {
            return null;
        }

        if (Path.IsPathRooted(executable) || ContainsDirectorySeparator(executable))
        {
            return ResolveCandidate(Path.GetFullPath(executable));
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidateName in CandidateNames(executable))
            {
                var candidate = ResolveCandidate(Path.Combine(directory, candidateName));
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool ContainsDirectorySeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar)
        || value.Contains(Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> CandidateNames(string executable)
    {
        yield return executable;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executable))
        {
            yield break;
        }

        var extensions = Environment.GetEnvironmentVariable("PATHEXT")
            ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [".COM", ".EXE", ".BAT", ".CMD"];
        foreach (var extension in extensions)
        {
            yield return executable + extension.ToLowerInvariant();
            yield return executable + extension.ToUpperInvariant();
        }
    }

    private static string? ResolveCandidate(string candidate)
    {
        if (!File.Exists(candidate))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(candidate);
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        try
        {
            var mode = File.GetUnixFileMode(fullPath);
            const UnixFileMode executableBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (mode & executableBits) != 0 ? fullPath : null;
        }
        catch (PlatformNotSupportedException)
        {
            return fullPath;
        }
    }
}
