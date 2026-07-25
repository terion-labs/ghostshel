namespace GhostShell.Packaging;

internal sealed record NativeMacOsBuildEvidenceCommand(
    NativeMacOsBuildEvidenceRequest Request,
    string OutputPath)
{
    private static readonly HashSet<string> Options =
    [
        "--artifact-libghostty",
        "--ghostty-install",
        "--ghostty-source",
        "--metallib",
        "--output",
        "--repository-root",
        "--sdk-directory",
        "--trace",
        "--zig-executable",
        "--zig-global-cache",
        "--zig-library-directory",
        "--zig-local-cache",
    ];

    public static NativeMacOsBuildEvidenceCommand Parse(
        IReadOnlyList<string> arguments)
    {
        var values = NativeMacOsEvidenceCommandParser.Parse(arguments, Options);
        return new NativeMacOsBuildEvidenceCommand(
            new NativeMacOsBuildEvidenceRequest(
                Required("--trace"),
                Required("--repository-root"),
                Required("--ghostty-source"),
                Required("--zig-executable"),
                Required("--zig-library-directory"),
                Required("--zig-local-cache"),
                Required("--zig-global-cache"),
                Required("--sdk-directory"),
                Required("--metallib"),
                Required("--ghostty-install"),
                Required("--artifact-libghostty")),
            Required("--output"));

        string Required(string name) =>
            NativeMacOsEvidenceCommandParser.Required(values, name);
    }
}

internal sealed record NativeMacOsResourceEvidenceCommand(
    NativeMacOsResourceEvidenceRequest Request,
    string OutputPath)
{
    private static readonly HashSet<string> Options =
    [
        "--ghostty-install",
        "--ghostty-source",
        "--output",
        "--zig-global-cache",
    ];

    public static NativeMacOsResourceEvidenceCommand Parse(
        IReadOnlyList<string> arguments)
    {
        var values = NativeMacOsEvidenceCommandParser.Parse(arguments, Options);
        return new NativeMacOsResourceEvidenceCommand(
            new NativeMacOsResourceEvidenceRequest(
                Required("--ghostty-source"),
                Required("--zig-global-cache"),
                Required("--ghostty-install")),
            Required("--output"));

        string Required(string name) =>
            NativeMacOsEvidenceCommandParser.Required(values, name);
    }
}

internal static class NativeMacOsEvidenceCommandParser
{
    public static IReadOnlyDictionary<string, string> Parse(
        IReadOnlyList<string> arguments,
        IReadOnlySet<string> allowedOptions)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count;)
        {
            var name = arguments[index++];
            if (!allowedOptions.Contains(name))
            {
                throw new PackagingUsageException($"Unknown option {name}.");
            }

            if (index >= arguments.Count)
            {
                throw new PackagingUsageException(
                    $"Option {name} requires a value.");
            }

            var value = arguments[index++];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new PackagingUsageException(
                    $"Option {name} cannot be empty.");
            }

            if (!values.TryAdd(name, value))
            {
                throw new PackagingUsageException(
                    $"Option {name} was supplied more than once.");
            }
        }

        return values;
    }

    public static string Required(
        IReadOnlyDictionary<string, string> values,
        string name) =>
        values.TryGetValue(name, out var value)
            ? value
            : throw new PackagingUsageException($"{name} is required.");
}

internal static class NativeMacOsEvidenceFilePublisher
{
    public static void Publish(string outputPath, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (content.IsEmpty
            || content.Length > NativeMacOsProvenanceSchema.MaximumReceiptBytes)
        {
            throw new InvalidDataException(
                "The normalized native evidence has an invalid byte length.");
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var parentPath = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException(
                "The evidence output requires a parent directory.",
                nameof(outputPath));
        var physicalParent = MacOsPackagePaths.RequireExistingDirectory(
            parentPath,
            "evidence output parent");
        var destination = Path.Combine(
            physicalParent,
            Path.GetFileName(fullOutputPath));
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException(
                "The evidence output already exists and will not be overwritten.");
        }

        var temporaryPath = Path.Combine(
            physicalParent,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destination);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
