using System.Formats.Tar;
using System.IO.Compression;
using GhostShell.Application.Previews;

namespace GhostShell.Previews;

/// <summary>
/// Lists an archive without unpacking it. A zip is answered from its central
/// directory — the index at the end of the file — so nothing is decompressed at
/// all. A tar has no index and must be walked, but only its headers are read:
/// each entry's data is skipped rather than extracted, and nothing is written
/// to disk either way.
/// </summary>
public sealed class ArchiveTableOfContents : IArchiveTableOfContents
{
    public bool Claims(string fileName) => ArchiveFormats.IsArchive(fileName);

    public async ValueTask<IReadOnlyList<ArchiveEntryDescriptor>?> ReadAsync(
        string path,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);

        try
        {
            return ArchiveFormats.Kind(path) switch
            {
                ArchiveKind.Zip => ReadZip(path, maximumEntries, cancellationToken),
                ArchiveKind.Tar => await ReadTarAsync(
                    path,
                    compressed: false,
                    maximumEntries,
                    cancellationToken).ConfigureAwait(false),
                ArchiveKind.CompressedTar => await ReadTarAsync(
                    path,
                    compressed: true,
                    maximumEntries,
                    cancellationToken).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A file that is not the archive its name claims, or one truncated
            // in transit, is a preview that cannot be shown — not a crash.
            return null;
        }
    }

    private static IReadOnlyList<ArchiveEntryDescriptor> ReadZip(
        string path,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entries = new List<ArchiveEntryDescriptor>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= maximumEntries)
            {
                break;
            }

            // A zip records a folder as an entry ending in a separator, with no
            // content of its own.
            var isDirectory = entry.FullName.EndsWith('/')
                || entry.FullName.EndsWith('\\');
            entries.Add(new ArchiveEntryDescriptor(
                entry.FullName,
                isDirectory,
                isDirectory ? null : entry.Length,
                isDirectory ? null : entry.CompressedLength));
        }

        return entries;
    }

    private static async ValueTask<IReadOnlyList<ArchiveEntryDescriptor>> ReadTarAsync(
        string path,
        bool compressed,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        await using var source = compressed
            ? new GZipStream(file, CompressionMode.Decompress)
            : (Stream)file;
        await using var reader = new TarReader(source, leaveOpen: true);
        var entries = new List<ArchiveEntryDescriptor>();
        while (entries.Count < maximumEntries)
        {
            var entry = await reader.GetNextEntryAsync(
                copyData: false,
                cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                break;
            }

            var isDirectory = entry.EntryType is TarEntryType.Directory;
            entries.Add(new ArchiveEntryDescriptor(
                entry.Name,
                isDirectory,
                isDirectory ? null : entry.Length,
                CompressedSize: null));
        }

        return entries;
    }
}
