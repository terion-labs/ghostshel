namespace GhostShell.Files;

public sealed class PosixLocalFileProvider : LocalFileProvider
{
    public PosixLocalFileProvider(LocalFileProviderOptions options)
        : base(
            options,
            OperatingSystem.IsMacOS()
                ? FileNameComparison.ProviderDefined
                : FileNameComparison.CaseSensitive,
            StringComparison.Ordinal)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The POSIX local file provider requires a Unix host.");
        }
    }

    protected override FileProviderError? ValidatePlatformSegment(FilePathSegment segment) => null;

    protected override bool IsHidden(FilePathSegment? name, FileAttributes attributes) =>
        name is { } value && value.Value.StartsWith(".", StringComparison.Ordinal);
}
