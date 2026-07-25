namespace GhostShell.Packaging;

internal sealed record NativeMacOsReceiptCommand(
    string CatalogPath,
    string ArtifactDirectory,
    string OutputPath,
    string RepositoryRoot,
    string GhosttySourceDirectory,
    string ZigArchivePath,
    string ZigExecutablePath,
    string ZigLibraryDirectory,
    string ZigLocalCacheDirectory,
    string ZigGlobalCacheDirectory,
    string ZigBuildTracePath,
    string GhosttyInstallDirectory,
    string ClangExecutablePath,
    string SdkDirectory,
    string SdkSettingsPath,
    string ReleaseArchivePath,
    string ReleaseArm64BinaryPath,
    string MetallibPath,
    string ArtifactLibGhosttyPath,
    string ZigVersion,
    string GhosttyCommit,
    string GhosttyTag,
    string ClangVersion,
    string SdkVersion,
    string? SdkBuild,
    IReadOnlyList<string> GhosttyOptions,
    IReadOnlyList<string> ShimCompilerOptions)
{
    private static readonly HashSet<string> SingularOptions =
    [
        "--artifact-directory",
        "--artifact-libghostty",
        "--catalog",
        "--clang-executable",
        "--clang-version",
        "--ghostty-commit",
        "--ghostty-install",
        "--ghostty-source",
        "--ghostty-tag",
        "--metallib",
        "--output",
        "--release-archive",
        "--release-arm64-binary",
        "--repository-root",
        "--sdk-directory",
        "--sdk-build",
        "--sdk-settings",
        "--sdk-version",
        "--zig-archive",
        "--zig-build-trace",
        "--zig-executable",
        "--zig-global-cache",
        "--zig-library-directory",
        "--zig-local-cache",
        "--zig-version",
    ];

    public static NativeMacOsReceiptCommand Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ghosttyOptions = new List<string>();
        var shimCompilerOptions = new List<string>();
        for (var index = 0; index < arguments.Count;)
        {
            var name = arguments[index++];
            if (index >= arguments.Count)
            {
                throw new PackagingUsageException(
                    $"Option {name} requires a value.");
            }

            var value = arguments[index++];
            if (name == "--ghostty-option")
            {
                AddRepeated(ghosttyOptions, value, name);
            }
            else if (name == "--shim-compiler-option")
            {
                AddRepeated(shimCompilerOptions, value, name);
            }
            else if (!SingularOptions.Contains(name))
            {
                throw new PackagingUsageException($"Unknown option {name}.");
            }
            else if (!values.TryAdd(name, value))
            {
                throw new PackagingUsageException(
                    $"Option {name} was supplied more than once.");
            }
        }

        if (ghosttyOptions.Count == 0 || shimCompilerOptions.Count == 0)
        {
            throw new PackagingUsageException(
                "native-macos-receipt requires observed Ghostty and shim options.");
        }

        return new NativeMacOsReceiptCommand(
            Required(values, "--catalog"),
            Required(values, "--artifact-directory"),
            Required(values, "--output"),
            Required(values, "--repository-root"),
            Required(values, "--ghostty-source"),
            Required(values, "--zig-archive"),
            Required(values, "--zig-executable"),
            Required(values, "--zig-library-directory"),
            Required(values, "--zig-local-cache"),
            Required(values, "--zig-global-cache"),
            Required(values, "--zig-build-trace"),
            Required(values, "--ghostty-install"),
            Required(values, "--clang-executable"),
            Required(values, "--sdk-directory"),
            Required(values, "--sdk-settings"),
            Required(values, "--release-archive"),
            Required(values, "--release-arm64-binary"),
            Required(values, "--metallib"),
            Required(values, "--artifact-libghostty"),
            Required(values, "--zig-version"),
            Required(values, "--ghostty-commit"),
            Required(values, "--ghostty-tag"),
            Required(values, "--clang-version"),
            Required(values, "--sdk-version"),
            Optional(values, "--sdk-build"),
            ghosttyOptions,
            shimCompilerOptions);
    }

    public NativeMacOsBuildReceiptRequest ToRequest() => new(
        CatalogPath,
        ArtifactDirectory,
        OutputPath,
        RepositoryRoot,
        GhosttySourceDirectory,
        ZigArchivePath,
        ZigExecutablePath,
        ZigLibraryDirectory,
        ZigLocalCacheDirectory,
        ZigGlobalCacheDirectory,
        ZigBuildTracePath,
        GhosttyInstallDirectory,
        ClangExecutablePath,
        SdkDirectory,
        SdkSettingsPath,
        ReleaseArchivePath,
        ReleaseArm64BinaryPath,
        MetallibPath,
        ArtifactLibGhosttyPath,
        ZigVersion,
        GhosttyCommit,
        GhosttyTag,
        ClangVersion,
        SdkVersion,
        SdkBuild,
        GhosttyOptions,
        ShimCompilerOptions);

    private static void AddRepeated(
        ICollection<string> destination,
        string value,
        string name)
    {
        if (string.IsNullOrWhiteSpace(value) || destination.Count >= 128)
        {
            throw new PackagingUsageException(
                $"Option {name} has an invalid number or value.");
        }

        destination.Add(value);
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new PackagingUsageException($"{name} is required.");
        }

        return value;
    }

    private static string? Optional(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PackagingUsageException($"{name} cannot be empty.");
        }

        return value;
    }
}
