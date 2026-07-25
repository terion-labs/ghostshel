namespace GhostShell.Files;

/// <summary>
/// Narrow, vendor-free seam used by hierarchical network providers. A session is single-operation
/// and single-threaded; the provider owns it and disposes it after all returned streams are closed.
/// </summary>
internal interface IRemoteHierarchicalFileSession : IAsyncDisposable
{
    ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask<RemoteFileEntry?> StatAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenReadAsync(
        string path,
        long offset,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenCreateNewAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    ValueTask RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);

    ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken);

    ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken);
}

internal interface IRemoteHierarchicalFileSessionFactory
{
    ValueTask<IRemoteHierarchicalFileSession> OpenAsync(CancellationToken cancellationToken);
}

internal sealed record RemoteFileEntry(
    string Name,
    FileEntryKind Kind,
    long? Size,
    DateTimeOffset? LastModifiedAt,
    string Revision,
    RemotePosixMetadata? PosixMetadata = null);

/// <summary>POSIX fields retained from SFTP even though the common contract cannot mutate them yet.</summary>
internal sealed record RemotePosixMetadata(int UserId, int GroupId, int Mode);
