using System.Security.Cryptography;
using System.Text.Json;

namespace GhostShell.Packaging;

internal sealed record CefMacOsBundleFile(
    string SourcePath,
    string DestinationRelativePath,
    long Length,
    string Sha256,
    UnixFileMode? UnixMode);

internal sealed record CefMacOsGeneratedFile(
    string DestinationRelativePath,
    byte[] Content);

internal sealed class CefMacOsBundlePlan
{
    private const string FrameworkName =
        "Chromium Embedded Framework.framework";

    private CefMacOsBundlePlan(
        IReadOnlyList<CefMacOsBundleFile> files,
        IReadOnlyList<CefMacOsGeneratedFile> generatedFiles)
    {
        Files = files;
        GeneratedFiles = generatedFiles;
    }

    public IReadOnlyList<CefMacOsBundleFile> Files { get; }

    public IReadOnlyList<CefMacOsGeneratedFile> GeneratedFiles { get; }

    public int FileCount => Files.Count + GeneratedFiles.Count;

    public long TotalBytes => checked(
        Files.Sum(file => file.Length)
        + GeneratedFiles.Sum(file => (long)file.Content.Length));

    public int MaximumRelativePathDepth => Files
        .Select(file => file.DestinationRelativePath)
        .Concat(GeneratedFiles.Select(file => file.DestinationRelativePath))
        .Max(PathDepth);

    public static CefMacOsBundlePlan Create(
        string runtimeRoot,
        string catalogPath,
        string rid)
    {
        if (rid is not ("osx-arm64" or "osx-x64"))
        {
            throw new ArgumentException(
                "A macOS CEF bundle requires osx-arm64 or osx-x64.",
                nameof(rid));
        }

        var inspection = CefRuntimeReceipt.Validate(runtimeRoot, catalogPath, rid);
        var files = new List<CefMacOsBundleFile>();
        foreach (var source in inspection.Files)
        {
            foreach (var destination in MapDestination(source.RelativePath))
            {
                files.Add(new CefMacOsBundleFile(
                    source.FullPath,
                    destination,
                    source.Length,
                    source.Sha256,
                    source.UnixMode));
            }
        }

        var generatedFiles = new CefMacOsGeneratedFile[]
        {
            new(
                "Resources/Licenses/Native/cef-runtime-components.json",
                inspection.Catalog.Content),
            new(
                "Resources/Licenses/Native/cef-runtime-build-receipt.json",
                inspection.ReceiptContent),
            new(
                "Resources/Licenses/CEF-SBOM.spdx.json",
                CreateSpdx(inspection)),
        };
        var destinations = files.Select(file => file.DestinationRelativePath)
            .Concat(generatedFiles.Select(file => file.DestinationRelativePath))
            .ToArray();
        if (destinations.Distinct(StringComparer.Ordinal).Count()
            != destinations.Length)
        {
            throw new InvalidDataException(
                "The CEF macOS bundle plan contains duplicate destinations.");
        }

        return new CefMacOsBundlePlan(
            files.OrderBy(
                    file => file.DestinationRelativePath,
                    StringComparer.Ordinal)
                .ToArray(),
            generatedFiles.OrderBy(
                    file => file.DestinationRelativePath,
                    StringComparer.Ordinal)
                .ToArray());
    }

    public void CopyTo(string contentsDirectory)
    {
        foreach (var file in Files)
        {
            var destination = Destination(contentsDirectory, file.DestinationRelativePath);
            CopyFile(file, destination);
        }

        foreach (var file in GeneratedFiles)
        {
            var destination = Destination(contentsDirectory, file.DestinationRelativePath);
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            output.Write(file.Content);
        }
    }

    private static IReadOnlyList<string> MapDestination(string sourcePath)
    {
        if (sourcePath == "libexclr8cef.dylib")
        {
            return
            [
                "MacOS/libexclr8cef.dylib",
                "Frameworks/libexclr8cef.dylib",
            ];
        }

        if (sourcePath == "CEF-LICENSE.txt")
        {
            return ["Resources/Licenses/CEF-LICENSE.txt"];
        }

        if (sourcePath == "CEF-CREDITS.html")
        {
            return ["Resources/Licenses/Chromium-CREDITS.html"];
        }

        if (sourcePath == "EXCLR8CEF-LICENSE.txt")
        {
            return ["Resources/Licenses/Exclr8CEF-MIT.txt"];
        }

        if (sourcePath.StartsWith($"{FrameworkName}/", StringComparison.Ordinal)
            || IsHelperPath(sourcePath))
        {
            return [$"Frameworks/{sourcePath}"];
        }

        throw new InvalidDataException(
            $"CEF macOS runtime file {sourcePath} has no bundle destination.");
    }

    private static bool IsHelperPath(string path)
    {
        var separator = path.IndexOf('/');
        if (separator <= 0)
        {
            return false;
        }

        var root = path[..separator];
        return root.StartsWith("GhostSHELL Helper", StringComparison.Ordinal)
            && root.EndsWith(".app", StringComparison.Ordinal);
    }

    private static void CopyFile(CefMacOsBundleFile file, string destination)
    {
        using var source = RegularPackageFileReader.Open(
            file.SourcePath,
            out var current);
        if (current.Length != file.Length || current.UnixMode != file.UnixMode)
        {
            throw new InvalidDataException(
                $"CEF runtime file {file.SourcePath} changed during assembly.");
        }

        using (var output = new FileStream(
                   destination,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 131_072,
                   FileOptions.SequentialScan))
        {
            source.CopyTo(output);
        }

        if (!OperatingSystem.IsWindows() && current.UnixMode is { } mode)
        {
            File.SetUnixFileMode(destination, mode);
        }

        using var copied = RegularPackageFileReader.Open(destination, out var result);
        var digest = Convert.ToHexString(SHA256.HashData(copied)).ToLowerInvariant();
        if (result.Length != file.Length || digest != file.Sha256)
        {
            throw new InvalidDataException(
                $"Packaged CEF runtime file {file.DestinationRelativePath} was corrupted.");
        }
    }

    private static string Destination(string contentsDirectory, string relativePath)
    {
        var destination = Path.Combine(
            contentsDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException(
                "A CEF bundle destination has no parent directory.");
        Directory.CreateDirectory(parent);
        return destination;
    }

    private static byte[] CreateSpdx(CefRuntimeInspection inspection)
    {
        var receiptSha256 = Convert.ToHexString(
                SHA256.HashData(inspection.ReceiptContent))
            .ToLowerInvariant();
        var distribution = inspection.Catalog.Distributions[inspection.Rid];
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("spdxVersion", "SPDX-2.3");
            writer.WriteString("dataLicense", "CC0-1.0");
            writer.WriteString("SPDXID", "SPDXRef-DOCUMENT");
            writer.WriteString(
                "name",
                $"GhostSHELL CEF runtime {inspection.Catalog.CefVersion} {inspection.Rid}");
            writer.WriteString(
                "documentNamespace",
                $"https://ghostshell.app/spdx/cef-runtime/{inspection.Rid}/{receiptSha256}");
            writer.WriteStartObject("creationInfo");
            writer.WriteString("created", inspection.Catalog.DocumentCreatedUtc);
            writer.WriteStartArray("creators");
            writer.WriteStringValue("Tool: GhostShell.Packaging-1.0.0");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("packages");
            WriteCefPackage(writer, inspection, distribution);
            WriteBindingPackage(writer, inspection);
            writer.WriteEndArray();
            writer.WriteStartArray("relationships");
            WriteRelationship(
                writer,
                "SPDXRef-DOCUMENT",
                "DESCRIBES",
                "SPDXRef-Package-CEF");
            WriteRelationship(
                writer,
                "SPDXRef-DOCUMENT",
                "DESCRIBES",
                "SPDXRef-Package-Exclr8CEF");
            WriteRelationship(
                writer,
                "SPDXRef-Package-Exclr8CEF",
                "DEPENDS_ON",
                "SPDXRef-Package-CEF");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return [.. buffer.ToArray(), (byte)'\n'];
    }

    private static void WriteCefPackage(
        Utf8JsonWriter writer,
        CefRuntimeInspection inspection,
        CefRuntimeDistribution distribution)
    {
        writer.WriteStartObject();
        writer.WriteString("name", "Chromium Embedded Framework");
        writer.WriteString("SPDXID", "SPDXRef-Package-CEF");
        writer.WriteString("versionInfo", inspection.Catalog.CefVersion);
        writer.WriteString(
            "downloadLocation",
            $"https://cef-builds.spotifycdn.com/cef_binary_"
            + $"{Uri.EscapeDataString(inspection.Catalog.CefVersion)}_"
            + $"{distribution.Platform}_minimal.tar.bz2");
        writer.WriteBoolean("filesAnalyzed", false);
        writer.WriteString("licenseConcluded", "NOASSERTION");
        writer.WriteString("licenseDeclared", "BSD-3-Clause");
        writer.WriteString("copyrightText", "NOASSERTION");
        writer.WriteStartArray("checksums");
        WriteChecksum(writer, "SHA1", inspection.ArchiveSha1);
        WriteChecksum(writer, "SHA256", inspection.ArchiveSha256);
        writer.WriteEndArray();
        writer.WriteString(
            "comment",
            "The adjacent reviewed catalog and build receipt bind the exact file closure. "
            + string.Join(" ", inspection.Catalog.ReleaseBlockers));
        writer.WriteEndObject();
    }

    private static void WriteBindingPackage(
        Utf8JsonWriter writer,
        CefRuntimeInspection inspection)
    {
        writer.WriteStartObject();
        writer.WriteString("name", "Exclr8CEF");
        writer.WriteString("SPDXID", "SPDXRef-Package-Exclr8CEF");
        writer.WriteString("versionInfo", inspection.Catalog.BindingVersion);
        writer.WriteString(
            "downloadLocation",
            $"{inspection.Catalog.BindingRepository}/tree/"
            + inspection.Catalog.BindingCommit);
        writer.WriteBoolean("filesAnalyzed", false);
        writer.WriteString("licenseConcluded", "NOASSERTION");
        writer.WriteString("licenseDeclared", "MIT");
        writer.WriteString("copyrightText", "NOASSERTION");
        writer.WriteString(
            "comment",
            $"Upstream commit: {inspection.Catalog.BindingCommit}. "
            + $"GhostSHELL source-snapshot SHA-256: {inspection.SourceSnapshotSha256}. "
            + $"Patch-set SHA-256: {inspection.PatchSetSha256}.");
        writer.WriteEndObject();
    }

    private static void WriteRelationship(
        Utf8JsonWriter writer,
        string source,
        string relationship,
        string target)
    {
        writer.WriteStartObject();
        writer.WriteString("spdxElementId", source);
        writer.WriteString("relationshipType", relationship);
        writer.WriteString("relatedSpdxElement", target);
        writer.WriteEndObject();
    }

    private static void WriteChecksum(
        Utf8JsonWriter writer,
        string algorithm,
        string value)
    {
        writer.WriteStartObject();
        writer.WriteString("algorithm", algorithm);
        writer.WriteString("checksumValue", value);
        writer.WriteEndObject();
    }

    private static int PathDepth(string path) =>
        path.Count(character => character == '/');
}
