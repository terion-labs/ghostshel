namespace GhostShell.Application;

/// <summary>
/// A file the operating system can open by path, for the lifetime of the lease.
///
/// A local provider hands back the file where it already lives; every other
/// provider streams it into a private temporary copy that is deleted when the
/// lease is disposed. Consumers must not assume which case they got, and must
/// not write through the path: a temporary copy's writes go nowhere, and a
/// local file's writes would bypass the provider's own mutation path.
/// </summary>
public sealed class MaterializedFile : IDisposable
{
    private readonly bool _isTemporary;
    private bool _disposed;

    public MaterializedFile(string path, bool isTemporary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _isTemporary = isTemporary;
    }

    /// <summary>An absolute path readable until this lease is disposed.</summary>
    public string Path { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_isTemporary)
        {
            return;
        }

        try
        {
            File.Delete(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that outlives its lease is a temp-directory cleanup
            // concern, never a reason to fail the caller's operation.
        }
    }
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
/// </summary>
public interface IFileContentMaterializer
{
    ValueTask<FilePanelResult<MaterializedFile>> MaterializeAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken);
}
