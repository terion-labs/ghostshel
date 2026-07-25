using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GhostShell.Packaging;

internal static class NativeMacOsArtifactManifestInspector
{
    private static readonly HashSet<string> RequiredBuildFiles =
    [
        "GHOSTTY-LICENSE",
        "ghostshell-ghostty-smoke",
        "libghostshell-ghostty.dylib",
        "libghostty.dylib",
    ];

    public static NativeMacOsArtifactManifest InspectBuildArtifacts(
        string artifactDirectory)
    {
        var root = MacOsPackagePaths.RequireExistingDirectory(
            artifactDirectory,
            nameof(artifactDirectory));
        var files = new List<NativeMacOsArtifactFile>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
        pending.Push((new DirectoryInfo(root), string.Empty));
        var entryCount = 0;
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            foreach (var entry in Enumerate(directory))
            {
                entryCount++;
                if (entryCount > NativeMacOsProvenanceSchema.MaximumArtifactEntries)
                {
                    throw new InvalidDataException(
                        "The native artifact tree exceeds the entry limit.");
                }

                RejectLink(entry);
                var relativePath = string.IsNullOrEmpty(prefix)
                    ? entry.Name
                    : $"{prefix}/{entry.Name}";
                NativeMacOsPath.Validate(relativePath);

                if (entry is DirectoryInfo childDirectory)
                {
                    if (!IsResourcePath(relativePath))
                    {
                        throw new InvalidDataException(
                            $"Unexpected native artifact directory {relativePath}.");
                    }

                    directories.Add(relativePath);
                    pending.Push((childDirectory, relativePath));
                    continue;
                }

                if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        "The native artifact tree contains an unsupported entry.");
                }

                if (relativePath == NativeMacOsProvenanceSchema.ReceiptFileName)
                {
                    EnsureRegularReceipt(entry.FullName);
                    continue;
                }

                var role = ClassifyArtifactPath(relativePath);
                if (files.Count
                    == NativeMacOsProvenanceSchema.MaximumArtifactFiles)
                {
                    throw new InvalidDataException(
                        "The native artifact tree exceeds the file limit.");
                }

                files.Add(HashFile(
                    entry.FullName,
                    relativePath,
                    role,
                    ref totalBytes));
            }
        }

        NativeMacOsPath.ValidatePortableUniqueness(directories);
        ValidateBuildShape(files, directories);
        return NativeMacOsArtifactManifest.Create(
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
    }

    public static NativeMacOsArtifactManifest InspectPackagedPayload(
        string executableDirectory,
        string licenseDirectory)
    {
        var executableRoot = MacOsPackagePaths.RequireExistingDirectory(
            executableDirectory,
            nameof(executableDirectory));
        var licenseRoot = MacOsPackagePaths.RequireExistingDirectory(
            licenseDirectory,
            nameof(licenseDirectory));
        long totalBytes = 0;
        var files = new List<NativeMacOsArtifactFile>();
        files.Add(HashFile(
            Path.Combine(executableRoot, "libghostshell-ghostty.dylib"),
            "libghostshell-ghostty.dylib",
            NativeMacOsArtifactRoles.Package,
            ref totalBytes));
        files.Add(HashFile(
            Path.Combine(executableRoot, "libghostty.dylib"),
            "libghostty.dylib",
            NativeMacOsArtifactRoles.Package,
            ref totalBytes));
        files.Add(HashFile(
            Path.Combine(licenseRoot, "GHOSTTY-LICENSE"),
            "GHOSTTY-LICENSE",
            NativeMacOsArtifactRoles.Package,
            ref totalBytes));
        var directories = new HashSet<string>(StringComparer.Ordinal);
        InspectPackagedResourceDirectory(
            executableRoot,
            "ghostty",
            files,
            directories,
            ref totalBytes);
        InspectPackagedResourceDirectory(
            executableRoot,
            "terminfo",
            files,
            directories,
            ref totalBytes);
        ValidateResourceDirectories(files, directories);

        if (files.Count > NativeMacOsProvenanceSchema.MaximumArtifactFiles)
        {
            throw new InvalidDataException(
                "The packaged native payload exceeds the file limit.");
        }

        var manifest = NativeMacOsArtifactManifest.Create(
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
        if (manifest.TotalBytes > NativeMacOsProvenanceSchema.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                "The packaged native payload exceeds the cumulative byte limit.");
        }

        return manifest;
    }

    private static void InspectPackagedResourceDirectory(
        string executableRoot,
        string name,
        ICollection<NativeMacOsArtifactFile> files,
        ISet<string> directories,
        ref long totalBytes)
    {
        var rootPath = Path.Combine(executableRoot, name);
        var root = new DirectoryInfo(rootPath);
        if (!root.Exists)
        {
            throw new InvalidDataException(
                $"The packaged native payload is missing {name}.");
        }

        RejectLink(root);
        directories.Add(name);
        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
        pending.Push((root, name));
        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            foreach (var entry in Enumerate(directory))
            {
                var nextEntryCount = checked(directories.Count + files.Count + 1);
                if (nextEntryCount
                    > NativeMacOsProvenanceSchema.MaximumArtifactEntries)
                {
                    throw new InvalidDataException(
                        "The packaged native payload exceeds the entry limit.");
                }

                RejectLink(entry);
                var relativePath = $"{prefix}/{entry.Name}";
                NativeMacOsPath.Validate(relativePath);
                if (entry is DirectoryInfo childDirectory)
                {
                    directories.Add(relativePath);
                    pending.Push((childDirectory, relativePath));
                    continue;
                }

                if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        "The packaged native payload contains an unsupported entry.");
                }

                if (files.Count
                    == NativeMacOsProvenanceSchema.MaximumArtifactFiles)
                {
                    throw new InvalidDataException(
                        "The packaged native payload exceeds the file limit.");
                }

                files.Add(HashFile(
                    entry.FullName,
                    relativePath,
                    NativeMacOsArtifactRoles.Package,
                    ref totalBytes));
            }
        }
    }

    private static IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists)
        {
            throw new InvalidDataException(
                "The native payload directory disappeared during inspection.");
        }

        RejectLink(directory);
        return directory.EnumerateFileSystemInfos(
            "*",
            new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            });
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null
            || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The native payload contains a symbolic link or reparse point.");
        }
    }

    internal static string ClassifyArtifactPath(string relativePath)
    {
        if (relativePath == "ghostshell-ghostty-smoke")
        {
            return NativeMacOsArtifactRoles.BuildTestBinary;
        }

        if (relativePath is "GHOSTTY-LICENSE"
            or "libghostshell-ghostty.dylib"
            or "libghostty.dylib"
            || relativePath.StartsWith("ghostty/", StringComparison.Ordinal)
            || relativePath.StartsWith("terminfo/", StringComparison.Ordinal))
        {
            return NativeMacOsArtifactRoles.Package;
        }

        throw new InvalidDataException(
            $"Unexpected native artifact file {relativePath}.");
    }

    private static bool IsResourcePath(string relativePath) =>
        relativePath is "ghostty" or "terminfo"
        || relativePath.StartsWith("ghostty/", StringComparison.Ordinal)
        || relativePath.StartsWith("terminfo/", StringComparison.Ordinal);

    private static NativeMacOsArtifactFile HashFile(
        string fullPath,
        string relativePath,
        string role,
        ref long cumulativeBytes)
    {
        NativeMacOsPath.Validate(relativePath);
        using var stream = RegularPackageFileReader.Open(
            fullPath,
            out var inspection);
        if (inspection.Length < 0
            || inspection.Length > NativeMacOsProvenanceSchema.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                $"Native payload file {relativePath} exceeds the byte limit.");
        }

        try
        {
            cumulativeBytes = checked(cumulativeBytes + inspection.Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "The native payload cumulative byte count overflowed.",
                exception);
        }

        if (cumulativeBytes > NativeMacOsProvenanceSchema.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                "The native payload exceeds the cumulative byte limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(131_072);
        try
        {
            var remaining = inspection.Length;
            while (remaining > 0)
            {
                var read = stream.Read(
                    buffer,
                    0,
                    (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"Native payload file {relativePath} became shorter while hashing.");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"Native payload file {relativePath} became longer while hashing.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new NativeMacOsArtifactFile(
            relativePath,
            role,
            inspection.Length,
            FormatUnixMode(inspection.UnixMode),
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void EnsureRegularReceipt(string path)
    {
        using var stream = RegularPackageFileReader.Open(path, out var inspection);
        if (inspection.Length > NativeMacOsProvenanceSchema.MaximumReceiptBytes)
        {
            throw new InvalidDataException(
                "The existing native build receipt exceeds the receipt byte limit.");
        }
    }

    private static string FormatUnixMode(UnixFileMode? unixMode)
    {
        if (unixMode is null)
        {
            // Windows test hosts cannot observe POSIX mode bits. Production receipt
            // generation is macOS-only and therefore always records the real mode.
            return "0000";
        }

        return Convert.ToString((int)unixMode.Value & 0xFFF, 8)
            .PadLeft(4, '0');
    }

    private static void ValidateBuildShape(
        IReadOnlyList<NativeMacOsArtifactFile> files,
        IReadOnlySet<string> directories)
    {
        var actualRootFiles = files
            .Where(file => !file.Path.Contains('/'))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualRootFiles.SetEquals(RequiredBuildFiles))
        {
            throw new InvalidDataException(
                "The native artifact tree does not contain the exact required root files.");
        }

        ValidateResourceDirectories(files, directories);
        var manifest = NativeMacOsArtifactManifest.Create(files);
        if (manifest.TotalBytes > NativeMacOsProvenanceSchema.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                "The native artifact tree exceeds the cumulative byte limit.");
        }
    }

    private static void ValidateResourceDirectories(
        IReadOnlyCollection<NativeMacOsArtifactFile> files,
        IReadOnlySet<string> directories)
    {
        var parentsWithFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var separator = file.Path.LastIndexOf('/');
            while (separator >= 0)
            {
                var parent = file.Path[..separator];
                parentsWithFiles.Add(parent);
                separator = parent.LastIndexOf('/');
            }
        }

        if (!directories.Contains("ghostty")
            || !directories.Contains("terminfo")
            || directories.Any(directory => !parentsWithFiles.Contains(directory)))
        {
            throw new InvalidDataException(
                "The native resource tree contains a missing or empty directory.");
        }
    }
}

internal static class NativeMacOsArtifactManifestDigester
{
    public static string Compute(IReadOnlyList<NativeMacOsArtifactFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            Append(hash, file.Path);
            Append(hash, file.Role);
            Append(hash, file.Length.ToString(CultureInfo.InvariantCulture));
            Append(hash, file.UnixMode);
            Append(hash, file.Sha256, finalField: true);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(
        IncrementalHash hash,
        string value,
        bool finalField = false)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(finalField ? [(byte)'\n'] : [(byte)0]);
    }
}

internal static class NativeMacOsPath
{
    public static void Validate(string path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Length > NativeMacOsProvenanceSchema.MaximumPathCharacters
            || path[0] == '/'
            || path.Contains('\\')
            || path.Contains(':')
            || path.Any(character => char.IsControl(character)))
        {
            throw new InvalidDataException(
                $"Native payload path {path} is not a safe relative path.");
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length > NativeMacOsProvenanceSchema.MaximumPathSegments
            || segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Native payload path {path} is not a safe relative path.");
        }
    }

    public static void ValidatePortableUniqueness(IEnumerable<string> paths)
    {
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            Validate(path);
            var normalized = path.Normalize(NormalizationForm.FormC);
            if (!portablePaths.Add(normalized))
            {
                throw new InvalidDataException(
                    $"Native payload path {path} collides on a portable filesystem.");
            }
        }
    }

    public static void ValidatePortableDirectoryUniqueness(
        IEnumerable<string> filePaths)
    {
        var exactDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in filePaths)
        {
            var separator = path.LastIndexOf('/');
            while (separator >= 0)
            {
                var directory = path[..separator];
                exactDirectories.Add(directory);
                separator = directory.LastIndexOf('/');
            }
        }

        ValidatePortableUniqueness(exactDirectories);
    }
}
