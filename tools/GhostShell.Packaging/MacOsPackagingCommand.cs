namespace GhostShell.Packaging;

internal sealed record MacOsPackagingCommand(
    string PublishDirectory,
    string DestinationPath,
    string ProductVersion,
    string BuildVersion,
    string ComponentCatalogPath,
    string NativeComponentCatalogPath,
    string NativeBuildReceiptPath,
    string NuGetPackageRoot)
{
    public static MacOsPackagingCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 16)
        {
            throw new PackagingUsageException(
                "macos requires --publish, --output, --version, --build-version, "
                + "--component-catalog, --native-component-catalog, "
                + "--native-build-receipt, and --nuget-packages.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            if (name is not (
                    "--publish"
                    or "--output"
                    or "--version"
                    or "--build-version"
                    or "--component-catalog"
                    or "--native-component-catalog"
                    or "--native-build-receipt"
                    or "--nuget-packages"))
            {
                throw new PackagingUsageException($"Unknown option {name}.");
            }

            if (!values.TryAdd(name, arguments[index + 1]))
            {
                throw new PackagingUsageException($"Option {name} was supplied more than once.");
            }
        }

        return new MacOsPackagingCommand(
            Required(values, "--publish"),
            Required(values, "--output"),
            Required(values, "--version"),
            Required(values, "--build-version"),
            Required(values, "--component-catalog"),
            Required(values, "--native-component-catalog"),
            Required(values, "--native-build-receipt"),
            Required(values, "--nuget-packages"));
    }

    public MacOsAppBundleRequest ToRequest() => new(
        PublishDirectory,
        DestinationPath,
        ProductVersion,
        BuildVersion,
        ComponentCatalogPath,
        NativeComponentCatalogPath,
        NativeBuildReceiptPath,
        NuGetPackageRoot);

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new PackagingUsageException($"{name} is required.");
        }

        return value;
    }
}

internal sealed class PackagingUsageException(string message) : Exception(message);
