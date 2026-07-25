using System.Collections.Immutable;

namespace GhostShell.Files;

public sealed record FilePage
{
    public FilePage(IEnumerable<FileEntry> items, FilePageToken? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToImmutableArray();
        ContinuationToken = continuationToken;
    }

    public ImmutableArray<FileEntry> Items { get; }

    public FilePageToken? ContinuationToken { get; }
}
