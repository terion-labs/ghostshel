namespace GhostShell.Files;

/// <summary>
/// Narrow, vendor-free seam used by hierarchical network providers. A session is single-operation
/// and single-threaded; the provider owns it and disposes it after all returned streams are closed.
/// </summary>
internal interface IRemoteHierarchicalFileSession : IAsyncDisposable
{
    /// <summary>
    /// True when <see cref="StatAsync"/> classifies the result as a link if any component in the
    /// requested path resolves through a symbolic link.
    /// </summary>
    bool StatDetectsAnyLinkInPath => false;

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

/// <summary>
/// Lets a transport use one protocol session for a provider-local copy while other transports
/// retain the default two-session behavior.
/// </summary>
internal interface IRemoteFileTransferSessionFactory
{
    ValueTask<RemoteFileTransferSessions> OpenTransferSessionsAsync(
        CancellationToken cancellationToken);
}

internal sealed class RemoteFileTransferSessions(
    IRemoteHierarchicalFileSession source,
    IRemoteHierarchicalFileSession destination) : IAsyncDisposable
{
    public IRemoteHierarchicalFileSession Source { get; } = source;

    public IRemoteHierarchicalFileSession Destination { get; } = destination;

    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Source, Destination))
        {
            await Source.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await Destination.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await Source.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A transport session that can survive multiple serialized provider operations.
/// Retryable protocol failures must make <see cref="CanReuse"/> false before they escape.
/// </summary>
internal interface IRetainableRemoteFileSession :
    IRemoteHierarchicalFileSession,
    IDisposable
{
    bool CanReuse { get; }
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
