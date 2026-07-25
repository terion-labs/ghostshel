namespace GhostShell.Files;

/// <summary>
/// Provider-neutral filesystem operations. Streams remain owned by the caller. Implementations
/// must honor request bounds, return typed expected failures, and never claim an optional
/// capability whose semantics they do not implement.
/// </summary>
public interface IFileProvider
{
    FileProviderProfileId ProfileId { get; }

    FileProviderCapabilities Capabilities { get; }

    ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken);
}
