using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GhostShell.Packaging;

internal sealed record NativeMacOsResourceEvidenceRequest(
    string GhosttySourceDirectory,
    string ZigGlobalCacheDirectory,
    string GhosttyInstallDirectory);

internal sealed record NativeMacOsResourceEvidenceResult(
    byte[] RawContent,
    string Sha256);

/// <summary>
/// Produces path-free evidence for the Ghostty runtime resources copied into
/// GhostSHELL. Source/tool claims are made only where byte-exact evidence exists.
/// </summary>
internal static class NativeMacOsResourceEvidenceBuilder
{
    private const int MaximumEntries = 4_096;
    private const int MaximumDepth = 16;
    private const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumEvidenceBytes = 2 * 1024 * 1024;
    private const int ExpectedThemeFileCount = 463;
    private const string ThemePackageKey =
        "N-V-__8AABVbAwBwDRyZONfx553tvMW8_A2OKUoLzPUSRiLF";

    private static readonly string[] ShellIntegrationPaths =
    [
        "bash/bash-preexec.sh",
        "bash/ghostty.bash",
        "elvish/lib/ghostty-integration.elv",
        "fish/vendor_conf.d/ghostty-shell-integration.fish",
        "nushell/vendor/autoload/ghostty.nu",
        "zsh/.zshenv",
        "zsh/ghostty-integration",
    ];

    private static readonly string[] TerminfoPaths =
    [
        "67/ghostty",
        "78/xterm-ghostty",
    ];

    public static NativeMacOsResourceEvidenceResult Observe(
        NativeMacOsResourceEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ghosttySource = MacOsPackagePaths.RequireExistingDirectory(
            request.GhosttySourceDirectory,
            nameof(request.GhosttySourceDirectory));
        var zigGlobalCache = MacOsPackagePaths.RequireExistingDirectory(
            request.ZigGlobalCacheDirectory,
            nameof(request.ZigGlobalCacheDirectory));
        var ghosttyInstall = MacOsPackagePaths.RequireExistingDirectory(
            request.GhosttyInstallDirectory,
            nameof(request.GhosttyInstallDirectory));
        var budget = new InspectionBudget();

        var sourceShell = InspectTree(
            RequireChildDirectory(
                ghosttySource,
                "src/shell-integration",
                "Ghostty shell-integration source"),
            budget);
        var installedShell = InspectTree(
            RequireChildDirectory(
                ghosttyInstall,
                "share/ghostty/shell-integration",
                "installed Ghostty shell integration"),
            budget);
        var shellFiles = ValidateShellIntegration(
            sourceShell,
            installedShell);

        var installedThemes = InspectTree(
            RequireChildDirectory(
                ghosttyInstall,
                "share/ghostty/themes",
                "installed Ghostty themes"),
            budget);
        if (installedThemes.Files.Count != ExpectedThemeFileCount)
        {
            throw new InvalidDataException(
                $"The installed Ghostty theme tree must contain exactly "
                + $"{ExpectedThemeFileCount} regular files.");
        }

        var themePackage = LocateThemePackage(
            RequireChildDirectory(
                zigGlobalCache,
                "p",
                "Zig global package cache"),
            installedThemes,
            budget);

        var terminfo = InspectTree(
            RequireChildDirectory(
                ghosttyInstall,
                "share/terminfo",
                "installed Ghostty terminfo"),
            budget);
        RequireExactTreeShape(
            terminfo,
            TerminfoPaths,
            "installed Ghostty terminfo");

        var content = WriteEvidence(shellFiles, installedThemes, themePackage, terminfo);
        var sha256 = Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();
        return new NativeMacOsResourceEvidenceResult(content, sha256);
    }

    private static IReadOnlyList<ObservedResourceFile> ValidateShellIntegration(
        ObservedResourceTree source,
        ObservedResourceTree installed)
    {
        RequireExactTreeShape(
            installed,
            ShellIntegrationPaths,
            "installed Ghostty shell integration");
        var installedPaths = installed.Files
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        var selectedSourceFiles = source.Files
            .Where(file =>
                !file.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && installedPaths.Contains(file.Path))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        if (selectedSourceFiles.Length != ShellIntegrationPaths.Length)
        {
            throw new InvalidDataException(
                "The Ghostty source tree does not contain every installed "
                + "shell-integration file.");
        }

        RequireExactFiles(
            selectedSourceFiles,
            installed.Files,
            "Ghostty shell-integration source and installed payload");
        return installed.Files;
    }

    private static ObservedThemePackage LocateThemePackage(
        DirectoryInfo packagesRoot,
        ObservedResourceTree installedThemes,
        InspectionBudget budget)
    {
        var expectedTopLevelNames = installedThemes.Files
            .Select(file => FirstSegment(file.Path))
            .Concat(installedThemes.Directories.Select(FirstSegment))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var packages = Enumerate(packagesRoot)
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        budget.AddEntries(packages.Length);
        NativeMacOsPath.ValidatePortableUniqueness(
            packages.Select(package => package.Name));

        var matches = new List<ObservedThemePackage>();
        foreach (var package in packages)
        {
            RejectLink(package);
            NativeMacOsPath.Validate(package.Name);
            if (package is not DirectoryInfo packageDirectory)
            {
                throw new InvalidDataException(
                    "The Zig package cache contains a non-directory entry.");
            }

            var directEntries = Enumerate(packageDirectory)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            budget.AddEntries(directEntries.Length);
            NativeMacOsPath.ValidatePortableUniqueness(
                directEntries.Select(entry => entry.Name));
            if (!directEntries
                    .Select(entry => entry.Name)
                    .SequenceEqual(expectedTopLevelNames, StringComparer.Ordinal))
            {
                continue;
            }

            var candidate = InspectTree(packageDirectory, budget);
            if (TreesMatch(candidate, installedThemes))
            {
                matches.Add(new ObservedThemePackage(
                    packageDirectory.Name,
                    candidate.Files));
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidDataException(
                "The Zig package cache must contain exactly one byte-exact "
                + "source package for the installed Ghostty themes.");
        }

        var match = matches[0];
        if (match.PackageKey != ThemePackageKey)
        {
            throw new InvalidDataException(
                "The byte-exact Ghostty theme package has an unexpected "
                + "Zig package key.");
        }

        return match;
    }

    private static ObservedResourceTree InspectTree(
        DirectoryInfo root,
        InspectionBudget budget)
    {
        RequireSafeDirectory(root, "resource root");
        var files = new List<ObservedResourceFile>();
        var directories = new List<string>();
        var pending = new Stack<(DirectoryInfo Directory, string Prefix, int Depth)>();
        pending.Push((root, string.Empty, 0));

        while (pending.Count > 0)
        {
            var (directory, prefix, depth) = pending.Pop();
            var entries = Enumerate(directory)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            budget.AddEntries(entries.Length);
            foreach (var entry in entries)
            {
                RejectLink(entry);
                var relativePath = string.IsNullOrEmpty(prefix)
                    ? entry.Name
                    : $"{prefix}/{entry.Name}";
                NativeMacOsPath.Validate(relativePath);
                var entryDepth = depth + 1;
                if (entryDepth > MaximumDepth)
                {
                    throw new InvalidDataException(
                        "The Ghostty resource tree exceeds the depth limit.");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    directories.Add(relativePath);
                    pending.Push((childDirectory, relativePath, entryDepth));
                    continue;
                }

                if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        "The Ghostty resource tree contains an unsupported entry.");
                }

                files.Add(HashRegularFile(
                    entry.FullName,
                    relativePath,
                    budget));
            }
        }

        var allPaths = files.Select(file => file.Path).Concat(directories);
        NativeMacOsPath.ValidatePortableUniqueness(allPaths);
        var orderedFiles = files
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        var orderedDirectories = directories
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return new ObservedResourceTree(orderedFiles, orderedDirectories);
    }

    private static ObservedResourceFile HashRegularFile(
        string fullPath,
        string relativePath,
        InspectionBudget budget)
    {
        using var stream = RegularPackageFileReader.Open(
            fullPath,
            out var inspection);
        budget.AddBytes(inspection.Length);
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
                        $"Ghostty resource {relativePath} became shorter "
                        + "during inspection.");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"Ghostty resource {relativePath} became longer "
                    + "during inspection.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ObservedResourceFile(
            relativePath,
            inspection.Length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void RequireExactTreeShape(
        ObservedResourceTree tree,
        IReadOnlyList<string> expectedPaths,
        string label)
    {
        var orderedExpectedPaths = expectedPaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!tree.Files
                .Select(file => file.Path)
                .SequenceEqual(orderedExpectedPaths, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The {label} file set does not match the expected runtime payload.");
        }

        var expectedDirectories = ParentDirectories(orderedExpectedPaths);
        if (!tree.Directories.SequenceEqual(
                expectedDirectories,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The {label} directory set does not match the expected runtime payload.");
        }
    }

    private static void RequireExactFiles(
        IReadOnlyList<ObservedResourceFile> first,
        IReadOnlyList<ObservedResourceFile> second,
        string label)
    {
        if (first.Count != second.Count)
        {
            throw new InvalidDataException($"{label} file counts differ.");
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index])
            {
                throw new InvalidDataException(
                    $"{label} differ at {second[index].Path}.");
            }
        }
    }

    private static bool TreesMatch(
        ObservedResourceTree first,
        ObservedResourceTree second)
    {
        if (!first.Directories.SequenceEqual(
                second.Directories,
                StringComparer.Ordinal)
            || first.Files.Count != second.Files.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Files.Count; index++)
        {
            if (first.Files[index] != second.Files[index])
            {
                return false;
            }
        }

        return true;
    }

    private static string[] ParentDirectories(IEnumerable<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var separator = path.LastIndexOf('/');
            while (separator >= 0)
            {
                var directory = path[..separator];
                directories.Add(directory);
                separator = directory.LastIndexOf('/');
            }
        }

        return directories.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static byte[] WriteEvidence(
        IReadOnlyList<ObservedResourceFile> shellFiles,
        ObservedResourceTree installedThemes,
        ObservedThemePackage themePackage,
        ObservedResourceTree terminfo)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", NativeMacOsProvenanceSchema.Version);
            writer.WriteString("generator", NativeMacOsProvenanceSchema.Generator);
            writer.WriteString(
                "evidenceKind",
                "ghostty-runtime-resource-provenance");
            writer.WriteString(
                "releaseReadiness",
                NativeMacOsProvenanceSchema.ReleaseReadiness);
            writer.WriteBoolean("legalClearance", false);
            writer.WriteString(
                "legalConclusion",
                NativeMacOsProvenanceSchema.LegalConclusion);

            writer.WriteStartObject("shellIntegration");
            writer.WriteString("sourceProvenanceStatus", "VERIFIED");
            writer.WriteString("installedPayloadStatus", "VERIFIED");
            writer.WriteString(
                "selectionBasis",
                "byte-exact-source-files-installed-by-relative-path");
            WriteManifest(writer, shellFiles);
            writer.WriteEndObject();

            writer.WriteStartObject("themes");
            writer.WriteString("sourceProvenanceStatus", "VERIFIED");
            writer.WriteString("installedPayloadStatus", "VERIFIED");
            writer.WriteString(
                "selectionBasis",
                "byte-exact-zig-package-installed-by-relative-path");
            writer.WriteString("packageKey", themePackage.PackageKey);
            WriteManifest(writer, installedThemes.Files);
            writer.WriteEndObject();

            writer.WriteStartObject("terminfo");
            writer.WriteString("evidenceKind", "artifact-observation");
            writer.WriteString("artifactObservationStatus", "VERIFIED");
            writer.WriteString("sourceProvenanceStatus", "BLOCKED");
            writer.WriteString("toolProvenanceStatus", "BLOCKED");
            WriteManifest(writer, terminfo.Files);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }

        stream.WriteByte((byte)'\n');
        if (stream.Length > MaximumEvidenceBytes)
        {
            throw new InvalidDataException(
                "The Ghostty resource evidence exceeds the byte limit.");
        }

        return stream.ToArray();
    }

    private static void WriteManifest(
        Utf8JsonWriter writer,
        IReadOnlyList<ObservedResourceFile> files)
    {
        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes = checked(totalBytes + file.Length);
        }

        writer.WriteNumber("fileCount", files.Count);
        writer.WriteNumber("totalBytes", totalBytes);
        writer.WriteString("manifestSha256", ComputeManifestSha256(files));
        writer.WriteStartArray("files");
        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteNumber("length", file.Length);
            writer.WriteString("sha256", file.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string ComputeManifestSha256(
        IReadOnlyList<ObservedResourceFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            AppendManifestField(hash, file.Path);
            AppendManifestField(
                hash,
                file.Length.ToString(CultureInfo.InvariantCulture));
            AppendManifestField(hash, file.Sha256, finalField: true);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendManifestField(
        IncrementalHash hash,
        string value,
        bool finalField = false)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(finalField ? [(byte)'\n'] : [(byte)0]);
    }

    private static DirectoryInfo RequireChildDirectory(
        string root,
        string portableRelativePath,
        string label)
    {
        NativeMacOsPath.Validate(portableRelativePath);
        var current = root;
        foreach (var segment in portableRelativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            RequireSafeDirectory(directory, label);
        }

        var relative = Path.GetRelativePath(root, current);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The {label} directory escapes its declared root.");
        }

        return new DirectoryInfo(current);
    }

    private static FileSystemInfo[] Enumerate(DirectoryInfo directory)
    {
        RequireSafeDirectory(directory, "resource directory");
        var entries = directory.EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                })
            .ToArray();
        RequireSafeDirectory(directory, "resource directory");
        return entries;
    }

    private static void RequireSafeDirectory(
        DirectoryInfo directory,
        string label)
    {
        directory.Refresh();
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException(
                $"The {label} directory does not exist.");
        }

        RejectLink(directory);
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null
            || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The Ghostty resource evidence contains a symbolic link "
                + "or reparse point.");
        }
    }

    private static string FirstSegment(string path)
    {
        var separator = path.IndexOf('/');
        return separator < 0 ? path : path[..separator];
    }

    private sealed class InspectionBudget
    {
        private int _entryCount;
        private long _byteCount;

        public void AddEntries(int count)
        {
            try
            {
                _entryCount = checked(_entryCount + count);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "The Ghostty resource entry count overflowed.",
                    exception);
            }

            if (_entryCount > MaximumEntries)
            {
                throw new InvalidDataException(
                    "The Ghostty resource evidence exceeds the entry limit.");
            }
        }

        public void AddBytes(long count)
        {
            if (count < 0)
            {
                throw new InvalidDataException(
                    "A Ghostty resource has a negative byte length.");
            }

            try
            {
                _byteCount = checked(_byteCount + count);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "The Ghostty resource byte count overflowed.",
                    exception);
            }

            if (_byteCount > MaximumBytes)
            {
                throw new InvalidDataException(
                    "The Ghostty resource evidence exceeds the cumulative "
                    + "byte limit.");
            }
        }
    }

    private sealed record ObservedResourceFile(
        string Path,
        long Length,
        string Sha256);

    private sealed record ObservedResourceTree(
        IReadOnlyList<ObservedResourceFile> Files,
        IReadOnlyList<string> Directories);

    private sealed record ObservedThemePackage(
        string PackageKey,
        IReadOnlyList<ObservedResourceFile> Files);
}
