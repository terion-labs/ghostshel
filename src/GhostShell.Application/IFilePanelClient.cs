namespace GhostShell.Application;

/// <summary>
/// Presentation-safe file operations. Concrete providers and their SDK payloads stay behind
/// the desktop composition boundary.
/// </summary>
public interface IFilePanelClient
{
    IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

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
}
