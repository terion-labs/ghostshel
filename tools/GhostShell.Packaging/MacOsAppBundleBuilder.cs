using System.Text;

namespace GhostShell.Packaging;

public sealed record MacOsAppBundleRequest(
    string PublishDirectory,
    string DestinationPath,
    string ProductVersion,
    string BuildVersion,
    string ComponentCatalogPath,
    string NativeComponentCatalogPath,
    string NativeBuildReceiptPath,
    string FontAssetsCatalogPath,
    string FontAssetsBuildReceiptPath,
    string NuGetPackageRoot,
    string? CefRuntimeRoot = null,
    string? CefRuntimeCatalogPath = null,
    string CefRuntimeIdentifier = "osx-arm64",
    string? ManagedEvidenceDirectory = null);

public sealed record MacOsAppBundleResult(
    string DestinationPath,
    string ProductVersion,
    string BuildVersion,
    int FileCount);

public sealed class MacOsAppBundleBuilder
{
    internal const int MaximumSourceFiles = 19_999;
    internal const int MaximumSourceEntries = 39_995;
    internal const int MaximumSourceDirectoryDepth = 62;
    internal const long MaximumPackageBytes = 8L * 1024 * 1024 * 1024;

    private const string InfoPlistResource =
        "GhostShell.Packaging.MacOS.Info.plist.template";
    private const string ProductVersionPlaceholder = "__GHOSTSHELL_VERSION__";
    private const string BuildVersionPlaceholder = "__GHOSTSHELL_BUILD_VERSION__";
    private const int NativeEvidenceDirectoryCount = 1;
    private const string NativeTerminalCatalogFileName =
        "native-terminal-components.json";
    private const string NativeTerminalReceiptFileName =
        "native-terminal-build-receipt.json";
    private const string TerminalFontCatalogFileName =
        "terminal-font-assets.json";
    private const string TerminalFontReceiptFileName =
        "terminal-font-assets-build-receipt.json";
    private const string TerminalFontLicenseFileName =
        "JETBRAINS-MONO-OFL.txt";

    private static readonly string[] RequiredRootFiles =
    [
        "GhostShell",
        "libghostty-vt.dylib",
        "GHOSTTY-LICENSE",
        "ghostty-vt-required-exports.txt",
        "THIRD-PARTY-NOTICES.md",
        "DOTNET-LICENSE.txt",
        "DOTNET-THIRD-PARTY-NOTICES.txt",
        NativeTerminalCatalogFileName,
        NativeTerminalReceiptFileName,
        TerminalFontCatalogFileName,
        TerminalFontReceiptFileName,
        TerminalFontLicenseFileName,
    ];

    private static readonly string[] RequiredClaudeNotificationFiles =
    [
        Path.Combine(
            "claude-plugins",
            "notifications",
            ".claude-plugin",
            "plugin.json"),
        Path.Combine(
            "claude-plugins",
            "notifications",
            "hooks",
            "hooks.json"),
        Path.Combine("ghostshell-cli-shims", "claude"),
        Path.Combine(
            "terminal-shell-integration",
            "bash",
            "ghostshell-claude.bash"),
        Path.Combine(
            "terminal-shell-integration",
            "zsh",
            ".zshenv"),
        Path.Combine(
            "terminal-shell-integration",
            "fish",
            "vendor_conf.d",
            "ghostshell-claude.fish"),
    ];

    private static readonly IReadOnlyDictionary<string, string>
        LicenseDestinations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GHOSTTY-LICENSE"] = "GHOSTTY-LICENSE",
            ["THIRD-PARTY-NOTICES.md"] = "THIRD-PARTY-NOTICES.md",
            ["DOTNET-LICENSE.txt"] = "DOTNET-LICENSE.txt",
            ["DOTNET-THIRD-PARTY-NOTICES.txt"] =
                "DOTNET-THIRD-PARTY-NOTICES.txt",
            [NativeTerminalCatalogFileName] =
                Path.Combine("Native", NativeTerminalCatalogFileName),
            [NativeTerminalReceiptFileName] =
                Path.Combine("Native", NativeTerminalReceiptFileName),
            [TerminalFontCatalogFileName] =
                Path.Combine("Native", TerminalFontCatalogFileName),
            [TerminalFontReceiptFileName] =
                Path.Combine("Native", TerminalFontReceiptFileName),
            [TerminalFontLicenseFileName] = "JetBrainsMono-OFL.txt",
        };

    public MacOsAppBundleResult Build(MacOsAppBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.CefRuntimeIdentifier, "osx-arm64", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Full macOS application packaging currently supports only "
                + "osx-arm64; osx-x64 lacks a reviewed managed catalog and "
                + "libghostty-vt receipt.",
                nameof(request));
        }

        ValidateVersion(request.ProductVersion, nameof(request.ProductVersion), 3, 3);
        ValidateVersion(request.BuildVersion, nameof(request.BuildVersion), 1, 3);

        var infoPlist = RenderInfoPlist(request.ProductVersion, request.BuildVersion);
        var infoPlistBytes = Encoding.UTF8.GetByteCount(infoPlist);
        var publishDirectory = MacOsPackagePaths.RequireExistingDirectory(
            request.PublishDirectory,
            nameof(request.PublishDirectory));
        var managedEvidenceDirectory = request.ManagedEvidenceDirectory is null
            ? publishDirectory
            : MacOsPackagePaths.RequireExistingDirectory(
                request.ManagedEvidenceDirectory,
                nameof(request.ManagedEvidenceDirectory));
        var hasSeparateManagedEvidence = !MacOsPackagePaths.AreSameDirectory(
            publishDirectory,
            managedEvidenceDirectory);
        var destinationPath = MacOsPackagePaths.RequireDestination(request.DestinationPath);
        MacOsPackagePaths.ValidateSeparateTrees(publishDirectory, destinationPath);
        if (hasSeparateManagedEvidence)
        {
            MacOsPackagePaths.ValidateSeparateTrees(
                publishDirectory,
                managedEvidenceDirectory);
            MacOsPackagePaths.ValidateSeparateTrees(
                managedEvidenceDirectory,
                destinationPath);
        }
        var cefPlan = CreateCefPlan(request, publishDirectory, destinationPath);

        var sourceEntries = InspectPublishDirectory(
            publishDirectory,
            infoPlistBytes);
        ValidateRequiredPayload(sourceEntries, hasSeparateManagedEvidence);
        var evidenceLimits = CreateManagedEvidenceLimits(
            sourceEntries,
            infoPlistBytes);

        var destinationParent = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException(
                "The macOS bundle destination must have a parent directory.",
                nameof(request));
        var stagingPath = Path.Combine(
            destinationParent,
            $".{MacOsPackagePaths.BundleName}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingPath);
        var published = false;
        try
        {
            var contentsDirectory = Path.Combine(stagingPath, "Contents");
            var executableDirectory = Path.Combine(contentsDirectory, "MacOS");
            var licenseDirectory = Path.Combine(contentsDirectory, "Resources", "Licenses");
            Directory.CreateDirectory(executableDirectory);
            Directory.CreateDirectory(licenseDirectory);

            CopyPublishPayload(
                executableDirectory,
                licenseDirectory,
                sourceEntries);
            ValidateNativeProvenance();
            var managedEvidence = ManagedComponentEvidenceBuilder.Build(
                managedEvidenceDirectory,
                licenseDirectory,
                request.ComponentCatalogPath,
                request.NuGetPackageRoot,
                evidenceLimits);
            EnsureEvidenceDestinationsAreAvailable(
                licenseDirectory,
                managedEvidence.Files);
            ValidateFinalBudget(
                sourceEntries,
                managedEvidence.Files,
                infoPlistBytes,
                cefPlan);
            WriteManagedEvidence(licenseDirectory, managedEvidence.Files);
            cefPlan?.CopyTo(contentsDirectory);
            File.WriteAllText(
                Path.Combine(contentsDirectory, "Info.plist"),
                infoPlist,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ValidateNativeProvenance();

            ExclusiveDirectoryMover.Move(stagingPath, destinationPath);
            published = true;
            return new MacOsAppBundleResult(
                destinationPath,
                request.ProductVersion,
                request.BuildVersion,
                sourceEntries.Count(entry => !entry.IsDirectory)
                + managedEvidence.Files.Count
                + (cefPlan?.FileCount ?? 0)
                + 1);

            void ValidateNativeProvenance()
            {
                NativeTerminalPackageProvenance.Validate(
                    executableDirectory,
                    licenseDirectory,
                    request.NativeComponentCatalogPath,
                    request.NativeBuildReceiptPath);
                TerminalFontPackageProvenance.Validate(
                    executableDirectory,
                    licenseDirectory,
                    request.FontAssetsCatalogPath,
                    request.FontAssetsBuildReceiptPath);
            }
        }
        finally
        {
            if (!published && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    internal static void ValidateSourceBudget(
        int fileCount,
        int entryCount,
        int maximumDirectoryDepth,
        long sourceBytes,
        int infoPlistBytes)
    {
        if (fileCount < 0 || fileCount > MaximumSourceFiles)
        {
            throw new InvalidDataException(
                $"The publish payload exceeds {MaximumSourceFiles} files.");
        }

        if (entryCount < 0 || entryCount > MaximumSourceEntries)
        {
            throw new InvalidDataException(
                $"The publish payload exceeds {MaximumSourceEntries} entries.");
        }

        if (maximumDirectoryDepth < 0
            || maximumDirectoryDepth > MaximumSourceDirectoryDepth)
        {
            throw new InvalidDataException(
                $"The publish payload exceeds {MaximumSourceDirectoryDepth} directory levels.");
        }

        if (infoPlistBytes < 0
            || sourceBytes < 0
            || sourceBytes > MaximumPackageBytes - infoPlistBytes)
        {
            throw new InvalidDataException(
                $"The finished application bundle exceeds {MaximumPackageBytes} bytes.");
        }
    }

    private static IReadOnlyList<SourceEntry> InspectPublishDirectory(
        string publishDirectory,
        int infoPlistBytes)
    {
        var root = new DirectoryInfo(publishDirectory);
        var entries = new List<SourceEntry>();
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));
        var entryCount = 0;
        var fileCount = 0;
        var maximumDirectoryDepth = 0;
        long sourceBytes = 0;

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions
                         {
                             AttributesToSkip = FileAttributes.None,
                             IgnoreInaccessible = false,
                             RecurseSubdirectories = false,
                             ReturnSpecialDirectories = false,
                         }))
            {
                entryCount++;
                if (entryCount > MaximumSourceEntries)
                {
                    ValidateSourceBudget(
                        fileCount,
                        entryCount,
                        maximumDirectoryDepth,
                        sourceBytes,
                        infoPlistBytes);
                }

                if (entry.LinkTarget is not null
                    || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        "The publish payload contains a symbolic link or reparse point.");
                }

                var relativePath = Path.GetRelativePath(root.FullName, entry.FullName);
                if (entry is DirectoryInfo childDirectory)
                {
                    var childDepth = depth + 1;
                    maximumDirectoryDepth = Math.Max(maximumDirectoryDepth, childDepth);
                    if (childDepth > MaximumSourceDirectoryDepth)
                    {
                        ValidateSourceBudget(
                            fileCount,
                            entryCount,
                            maximumDirectoryDepth,
                            sourceBytes,
                            infoPlistBytes);
                    }

                    entries.Add(new SourceEntry(
                        entry.FullName,
                        relativePath,
                        IsDirectory: true,
                        Length: 0,
                        UnixMode: null));
                    pending.Push((childDirectory, childDepth));
                    continue;
                }

                if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        "The publish payload contains an unsupported filesystem entry.");
                }

                fileCount++;
                if (fileCount > MaximumSourceFiles)
                {
                    ValidateSourceBudget(
                        fileCount,
                        entryCount,
                        maximumDirectoryDepth,
                        sourceBytes,
                        infoPlistBytes);
                }

                using var stream = RegularPackageFileReader.Open(
                    entry.FullName,
                    out var inspection);
                try
                {
                    sourceBytes = checked(sourceBytes + inspection.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException(
                        "The publish payload byte count overflowed.",
                        exception);
                }

                ValidateSourceBudget(
                    fileCount,
                    entryCount,
                    maximumDirectoryDepth,
                    sourceBytes,
                    infoPlistBytes);
                entries.Add(new SourceEntry(
                    entry.FullName,
                    relativePath,
                    IsDirectory: false,
                    inspection.Length,
                    inspection.UnixMode));
            }
        }

        ValidateSourceBudget(
            fileCount,
            entryCount,
            maximumDirectoryDepth,
            sourceBytes,
            infoPlistBytes);
        return entries;
    }

    private static void ValidateRequiredPayload(
        IReadOnlyList<SourceEntry> entries,
        bool hasSeparateManagedEvidence)
    {
        var rootFiles = entries
            .Where(entry =>
                !entry.IsDirectory
                && string.IsNullOrEmpty(Path.GetDirectoryName(entry.RelativePath)))
            .ToDictionary(
                entry => Path.GetFileName(entry.RelativePath),
                StringComparer.Ordinal);
        foreach (var requiredFile in RequiredRootFiles)
        {
            if (!rootFiles.ContainsKey(requiredFile))
            {
                throw new InvalidDataException(
                    $"The publish payload is missing required file {requiredFile}.");
            }
        }

        if (hasSeparateManagedEvidence)
        {
            var managedHostEntry = entries.FirstOrDefault(entry =>
                !entry.IsDirectory
                && (entry.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || entry.RelativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                    || entry.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                    || entry.RelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)));
            if (managedHostEntry is not null)
            {
                throw new InvalidDataException(
                    $"The Native AOT publish contains managed host file {managedHostEntry.RelativePath}.");
            }
        }
        else
        {
            foreach (var managedHostFile in new[]
                     {
                         "GhostShell.deps.json",
                         "GhostShell.runtimeconfig.json",
                     })
            {
                if (!rootFiles.ContainsKey(managedHostFile))
                {
                    throw new InvalidDataException(
                        $"The publish payload is missing required file {managedHostFile}.");
                }
            }
        }

        var payloadFiles = entries
            .Where(entry => !entry.IsDirectory)
            .ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        foreach (var requiredFile in RequiredClaudeNotificationFiles)
        {
            if (!payloadFiles.ContainsKey(requiredFile))
            {
                throw new InvalidDataException(
                    $"The publish payload is missing required file {requiredFile}.");
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            if ((rootFiles["GhostShell"].UnixMode & executeBits) == 0)
            {
                throw new InvalidDataException(
                    "The published GhostShell executable lacks an execute bit.");
            }

            var claudeShim = payloadFiles[Path.Combine(
                "ghostshell-cli-shims",
                "claude")];
            if ((claudeShim.UnixMode & executeBits) == 0)
            {
                throw new InvalidDataException(
                    "The published Claude shim lacks an execute bit.");
            }
        }
    }

    private static void CopyPublishPayload(
        string executableDirectory,
        string licenseDirectory,
        IReadOnlyList<SourceEntry> entries)
    {
        foreach (var directory in entries
                     .Where(entry => entry.IsDirectory)
                     .OrderBy(entry => PathDepth(entry.RelativePath)))
        {
            Directory.CreateDirectory(Path.Combine(
                executableDirectory,
                directory.RelativePath));
        }

        foreach (var file in entries.Where(entry => !entry.IsDirectory))
        {
            var fileName = Path.GetFileName(file.RelativePath);
            var isRootFile =
                string.IsNullOrEmpty(Path.GetDirectoryName(file.RelativePath));
            var destination = isRootFile
                && LicenseDestinations.TryGetValue(
                    fileName,
                    out var licenseDestination)
                    ? Path.Combine(licenseDirectory, licenseDestination)
                    : Path.Combine(executableDirectory, file.RelativePath);
            var destinationParent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException(
                    "A publish payload entry has no destination parent.");
            Directory.CreateDirectory(destinationParent);

            using var source = RegularPackageFileReader.Open(
                file.FullPath,
                out var current);
            if (current.Length != file.Length || current.UnixMode != file.UnixMode)
            {
                throw new InvalidDataException(
                    "The publish payload changed while the bundle was assembled.");
            }

            using (var target = new FileStream(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 131_072,
                       FileOptions.SequentialScan))
            {
                CopyExactly(source, target, file.Length);
            }

            if (!OperatingSystem.IsWindows()
                && current.UnixMode is { } unixMode)
            {
                File.SetUnixFileMode(destination, unixMode);
            }
        }
    }

    private static void ValidateFinalBudget(
        IReadOnlyList<SourceEntry> sourceEntries,
        IReadOnlyList<ManagedComponentEvidenceFile> evidenceFiles,
        int infoPlistBytes,
        CefMacOsBundlePlan? cefPlan)
    {
        var evidenceDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidenceFile in evidenceFiles)
        {
            var parent = Path.GetDirectoryName(evidenceFile.RelativePath);
            while (!string.IsNullOrEmpty(parent))
            {
                evidenceDirectories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        var maximumEvidenceDepth = evidenceFiles.Count == 0
            ? 0
            : evidenceFiles.Max(file => PathDepth(file.RelativePath));
        var maximumSourceDepth = sourceEntries.Count == 0
            ? 0
            : sourceEntries.Max(entry => PathDepth(entry.RelativePath));
        long bytes;
        try
        {
            bytes = checked(
                sourceEntries.Where(entry => !entry.IsDirectory)
                    .Sum(entry => entry.Length)
                + evidenceFiles.Sum(file => (long)file.Content.Length)
                + (cefPlan?.TotalBytes ?? 0));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "The finished application bundle byte count overflowed.",
                exception);
        }

        ValidateSourceBudget(
            sourceEntries.Count(entry => !entry.IsDirectory)
            + evidenceFiles.Count
            + (cefPlan?.FileCount ?? 0),
            sourceEntries.Count
            + evidenceFiles.Count
            + evidenceDirectories.Count
            + NativeEvidenceDirectoryCount
            + CountCefDirectories(cefPlan),
            Math.Max(
                Math.Max(
                    Math.Max(maximumSourceDepth, maximumEvidenceDepth),
                    cefPlan?.MaximumRelativePathDepth ?? 0),
                NativeEvidenceDirectoryCount),
            bytes,
            infoPlistBytes);
    }

    private static CefMacOsBundlePlan? CreateCefPlan(
        MacOsAppBundleRequest request,
        string publishDirectory,
        string destinationPath)
    {
        if (request.CefRuntimeRoot is null && request.CefRuntimeCatalogPath is null)
        {
            return null;
        }

        if (request.CefRuntimeRoot is null || request.CefRuntimeCatalogPath is null)
        {
            throw new ArgumentException(
                "The CEF runtime root and catalog must be supplied together.",
                nameof(request));
        }

        var runtimeRoot = MacOsPackagePaths.RequireExistingDirectory(
            request.CefRuntimeRoot,
            nameof(request.CefRuntimeRoot));
        MacOsPackagePaths.ValidateSeparateTrees(runtimeRoot, publishDirectory);
        MacOsPackagePaths.ValidateSeparateTrees(runtimeRoot, destinationPath);
        return CefMacOsBundlePlan.Create(
            runtimeRoot,
            request.CefRuntimeCatalogPath,
            request.CefRuntimeIdentifier);
    }

    private static int CountCefDirectories(CefMacOsBundlePlan? plan)
    {
        if (plan is null)
        {
            return 0;
        }

        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in plan.Files.Select(file => file.DestinationRelativePath)
                     .Concat(plan.GeneratedFiles.Select(file => file.DestinationRelativePath)))
        {
            var parent = Path.GetDirectoryName(path.Replace(
                '/',
                Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        return directories.Count;
    }

    private static ManagedComponentEvidenceLimits CreateManagedEvidenceLimits(
        IReadOnlyList<SourceEntry> sourceEntries,
        int infoPlistBytes)
    {
        var sourceFileCount = sourceEntries.Count(entry => !entry.IsDirectory);
        var sourceBytes = sourceEntries
            .Where(entry => !entry.IsDirectory)
            .Sum(entry => entry.Length);
        var remainingBytes = MaximumPackageBytes - infoPlistBytes - sourceBytes;
        var remainingFiles = MaximumSourceFiles - sourceFileCount;
        var remainingEntries = MaximumSourceEntries
            - sourceEntries.Count
            - NativeEvidenceDirectoryCount;
        if (remainingFiles < 1 || remainingEntries < 1 || remainingBytes < 1)
        {
            throw new InvalidDataException(
                "The publish payload leaves no room for required managed evidence.");
        }

        return new ManagedComponentEvidenceLimits(
            Math.Min(
                ManagedComponentEvidenceBuilder.MaximumGeneratedEvidenceFiles,
                remainingFiles),
            remainingEntries,
            Math.Min(
                ManagedComponentEvidenceBuilder.MaximumGeneratedEvidenceBytes,
                remainingBytes),
            MaximumSourceDirectoryDepth - 1);
    }

    private static void WriteManagedEvidence(
        string licenseDirectory,
        IReadOnlyList<ManagedComponentEvidenceFile> evidenceFiles)
    {
        foreach (var evidenceFile in evidenceFiles)
        {
            var destination = Path.Combine(
                licenseDirectory,
                evidenceFile.RelativePath);
            var destinationParent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException(
                    "A managed evidence file has no destination parent.");
            Directory.CreateDirectory(destinationParent);
            using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 131_072,
                FileOptions.SequentialScan);
            target.Write(evidenceFile.Content);
        }
    }

    private static void EnsureEvidenceDestinationsAreAvailable(
        string licenseDirectory,
        IReadOnlyList<ManagedComponentEvidenceFile> evidenceFiles)
    {
        foreach (var evidenceFile in evidenceFiles)
        {
            var destination = Path.Combine(
                licenseDirectory,
                evidenceFile.RelativePath);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new InvalidDataException(
                    $"Generated evidence path {evidenceFile.RelativePath} "
                    + "collides with the publish payload.");
            }

            var parent = Path.GetDirectoryName(destination);
            while (parent is not null
                   && !string.Equals(
                       parent,
                       licenseDirectory,
                       StringComparison.Ordinal))
            {
                if (File.Exists(parent))
                {
                    throw new InvalidDataException(
                        $"Generated evidence path {evidenceFile.RelativePath} "
                        + "collides with the publish payload.");
                }

                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    private static void CopyExactly(Stream source, Stream destination, long length)
    {
        var buffer = new byte[131_072];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(
                buffer,
                0,
                (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new InvalidDataException(
                    "A publish payload file became shorter while it was copied.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }

        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "A publish payload file became longer while it was copied.");
        }
    }

    private static int PathDepth(string path) =>
        path.Count(character =>
            character == Path.DirectorySeparatorChar
            || character == Path.AltDirectorySeparatorChar);

    private static string RenderInfoPlist(
        string productVersion,
        string buildVersion)
    {
        using var stream = typeof(MacOsAppBundleBuilder).Assembly
            .GetManifestResourceStream(InfoPlistResource)
            ?? throw new InvalidOperationException(
                "The embedded macOS Info.plist template is unavailable.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var template = reader.ReadToEnd();
        if (CountOccurrences(template, ProductVersionPlaceholder) != 1
            || CountOccurrences(template, BuildVersionPlaceholder) != 1)
        {
            throw new InvalidDataException(
                "The embedded macOS Info.plist template has invalid placeholders.");
        }

        return template
            .Replace(
                ProductVersionPlaceholder,
                productVersion,
                StringComparison.Ordinal)
            .Replace(
                BuildVersionPlaceholder,
                buildVersion,
                StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static void ValidateVersion(
        string value,
        string parameterName,
        int minimumParts,
        int maximumParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 32)
        {
            throw new ArgumentException(
                "The bundle version is too long.",
                parameterName);
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length < minimumParts
            || parts.Length > maximumParts
            || parts.Any(part =>
                part.Length == 0
                || part.Length > 9
                || !part.All(character => character is >= '0' and <= '9')))
        {
            throw new ArgumentException(
                "The bundle version must contain dot-separated unsigned integers.",
                parameterName);
        }
    }

    private sealed record SourceEntry(
        string FullPath,
        string RelativePath,
        bool IsDirectory,
        long Length,
        UnixFileMode? UnixMode);
}
