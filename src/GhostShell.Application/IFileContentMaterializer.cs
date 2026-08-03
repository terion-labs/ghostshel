namespace GhostShell.Application;

/// <summary>
/// A file the operating system can open by path.
///
/// A local provider hands back the file where it already lives; every other
/// provider is served from a cache of downloaded copies. Consumers must not
/// write through the path: a cached copy's writes go nowhere, and a local
/// file's writes would bypass the provider's own mutation path.
/// </summary>
public sealed record MaterializedFile(string Path, bool IsCachedCopy)
{
    /// <summary>An absolute path readable by the operating system.</summary>
    public string Path { get; } = string.IsNullOrWhiteSpace(Path)
        ? throw new ArgumentException("A materialized file needs a path.", nameof(Path))
        : Path;

    /// <summary>
    /// Whether the path is a downloaded copy rather than the user's own file.
    /// A copy is a snapshot: it does not follow later changes to the source.
    /// </summary>
    public bool IsCachedCopy { get; } = IsCachedCopy;
}

/// <summary>
/// Produces a real filesystem path for a file location, for consumers that
/// cannot work from a bounded byte preview — a database engine opening a SQLite
/// file has to hand a path to its driver.
///
/// This is deliberately separate from previewing: it is the one operation that
/// pulls a whole file, so it is bounded by an explicit byte ceiling and is
/// refused rather than truncated when the file exceeds it. A truncated database
/// is not a smaller database; it is a corrupt one.
///
/// A downloaded copy is cached on disk and reused: the same file selected twice
/// is fetched once. The cache has no index — the copy's own path is derived
/// from the file's identity, so its presence on disk is the record.
/// </summary>
public interface IFileContentMaterializer
{
    ValueTask<FilePanelResult<MaterializedFile>> MaterializeAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken);
}
