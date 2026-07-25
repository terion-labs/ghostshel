using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Provider-neutral file operations owned by one hosted panel session. Implementations may share
/// provider runtimes, but transfers exposed through this boundary belong only to this session.
/// </summary>
public interface IFilePanelSession : IPanelSession
{
    FileSessionMetadata Metadata { get; }

    IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; }

    ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<Unit>> CancelTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken);
}
