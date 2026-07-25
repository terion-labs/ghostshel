using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost.Tests;

internal sealed class FakeFilePanelSessionFactory : IFilePanelSessionFactory
{
    private readonly Dictionary<SessionId, FakeFilePanelSession> _sessions = [];

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.FilesList,
        SessionCapabilities.FilesStat,
        SessionCapabilities.FilesPreview,
        SessionCapabilities.FilesCreateDirectory,
        SessionCapabilities.FilesRename,
        SessionCapabilities.FilesDelete,
        SessionCapabilities.FilesTransferEnqueue,
        SessionCapabilities.FilesTransferCancel,
        SessionCapabilities.FilesTransferRetry,
    ]);

    public Func<FilePanelLocation, FileSessionMetadata>? MetadataFactory { get; set; }

    public FakeFilePanelSession this[SessionId id] => _sessions[id];

    public ValueTask<IFilePanelSession> CreateAsync(
        SessionId sessionId,
        FilePanelLocation initialLocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = new FakeFilePanelSession(
            sessionId,
            initialLocation,
            Capabilities,
            MetadataFactory?.Invoke(initialLocation));
        _sessions.Add(sessionId, session);
        return ValueTask.FromResult<IFilePanelSession>(session);
    }
}

internal sealed class FakeFilePanelSession : IFilePanelSession
{
    private readonly List<FilePanelTransferSnapshot> _transfers = [];
    private bool _closed;

    public FakeFilePanelSession(
        SessionId id,
        FilePanelLocation initialLocation,
        CapabilitySet capabilities,
        FileSessionMetadata? metadata = null)
    {
        Id = id;
        InitialLocation = initialLocation;
        Capabilities = capabilities;
        Metadata = metadata ?? new FileSessionMetadata(
            initialLocation,
            FilePanelCapability.List
            | FilePanelCapability.Stat
            | FilePanelCapability.RangedRead,
            maximumListPageSize: 1_000,
            maximumPreviewBytes: 1024 * 1024);
    }

    public SessionId Id { get; }

    public FilePanelLocation InitialLocation { get; }

    public FileSessionMetadata Metadata { get; private set; }

    public PanelKind Kind => PanelKind.FileViewer;

    public CapabilitySet Capabilities { get; private set; }

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers => _transfers.ToArray();

    public int CreateDirectoryCount { get; private set; }

    public int DeleteCount { get; private set; }

    public int ListCount { get; private set; }

    public int StatCount { get; private set; }

    public int PreviewCount { get; private set; }

    public FilePanelListRequest? LastListRequest { get; private set; }

    public FilePanelLocation? LastStatLocation { get; private set; }

    public FilePanelPreviewRequest? LastPreviewRequest { get; private set; }

    public FilePanelCreateDirectoryRequest? LastCreateDirectoryRequest { get; private set; }

    public FilePanelDeleteRequest? LastDeleteRequest { get; private set; }

    public Func<
        FilePanelListRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelPage>>>? ListOperation
    { get; set; }

    public Func<
        FilePanelLocation,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelEntry>>>? StatOperation
    { get; set; }

    public Func<
        FilePanelPreviewRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelPreview>>>? PreviewOperation
    { get; set; }

    public Func<
        FilePanelCreateDirectoryRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelEntry>>>? CreateDirectoryOperation
    { get; set; }

    public Func<
        FilePanelDeleteRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelDeleteReceipt>>>? DeleteOperation
    { get; set; }

    public PanelCloseMode? LastCloseMode { get; private set; }

    public void ReplaceMetadata(FileSessionMetadata metadata) =>
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    public void RemoveCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        Capabilities = new CapabilitySet(
            Capabilities.Values.Where(item =>
                !string.Equals(item, capability, StringComparison.Ordinal)));
    }

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListCount++;
        LastListRequest = request;
        if (ListOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(new FilePanelPage(
            [Entry(request.Location.Child(new FilePanelPathSegment("listed.txt")), "listed.txt")],
            null)));
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatCount++;
        LastStatLocation = location;
        if (StatOperation is { } operation)
        {
            return operation(location, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(
            Entry(location, LocationName(location, "stat.txt"))));
    }

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreviewCount++;
        LastPreviewRequest = request;
        if (PreviewOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelPreview>.Success(new FilePanelPreview(
            request.Location,
            FilePanelPreviewKind.Text,
            "text/plain",
            "preview"u8,
            false)));
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateDirectoryCount++;
        LastCreateDirectoryRequest = request;
        if (CreateDirectoryOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(new FilePanelEntry(
            request.Location,
            request.Location.Address is FilePanelAddress.Hierarchical hierarchical
                ? hierarchical.Path.Name?.Value ?? "directory"
                : "directory",
            FilePanelEntryKind.Directory,
            null,
            null,
            false)));
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(Entry(
            request.Destination,
            "renamed.txt")));
    }

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCount++;
        LastDeleteRequest = request;
        if (DeleteOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelDeleteReceipt>.Success(
            new FilePanelDeleteReceipt(request.Location, false)));
    }

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = TransferSnapshot(request);
        _transfers.Add(snapshot);
        return ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Success(snapshot));
    }

    public ValueTask<FilePanelResult<Unit>> CancelTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _transfers.FindIndex(item => item.Id == id);
        _transfers[index] = _transfers[index] with
        {
            State = FilePanelTransferState.Cancelled,
            Stage = "Cancelled",
            CompletedAt = DateTimeOffset.UtcNow,
        };
        return ValueTask.FromResult(FilePanelResult<Unit>.Success(Unit.Value));
    }

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = _transfers.Single(item => item.Id == id).Request;
        var snapshot = TransferSnapshot(request);
        _transfers.Add(snapshot);
        return ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Success(snapshot));
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = _transfers.Count(item => item.CanCancel);
        return ValueTask.FromResult(_closed
            ? new PanelSessionSnapshot(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                false,
                "Closed")
            : new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                active > 0,
                active > 0 ? $"{active} active transfer(s)." : "Ready"));
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = afterSequence;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
        {
            return ValueTask.FromResult(PanelCloseOutcome.AlreadyClosed);
        }

        LastCloseMode = mode;
        if (mode == PanelCloseMode.Graceful && _transfers.Any(item => item.CanCancel))
        {
            return ValueTask.FromResult(PanelCloseOutcome.ConfirmationRequired);
        }

        if (mode == PanelCloseMode.Force)
        {
            for (var index = 0; index < _transfers.Count; index++)
            {
                if (_transfers[index].CanCancel)
                {
                    _transfers[index] = _transfers[index] with
                    {
                        State = FilePanelTransferState.Cancelled,
                        Stage = "Cancelled",
                        CompletedAt = DateTimeOffset.UtcNow,
                    };
                }
            }
        }

        _closed = true;
        return ValueTask.FromResult(mode == PanelCloseMode.Force
            ? PanelCloseOutcome.ForceTerminated
            : PanelCloseOutcome.GracefullyClosed);
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        return ValueTask.CompletedTask;
    }

    private static FilePanelEntry Entry(FilePanelLocation location, string name) => new(
        location,
        name,
        FilePanelEntryKind.File,
        7,
        null,
        false);

    private static string LocationName(
        FilePanelLocation location,
        string fallback) =>
        location.Address is FilePanelAddress.Hierarchical hierarchical
            ? hierarchical.Path.Name?.Value ?? fallback
            : fallback;

    private static FilePanelTransferSnapshot TransferSnapshot(FilePanelTransferRequest request) => new(
        FilePanelTransferId.New(),
        request,
        request.Destination,
        FilePanelTransferState.Running,
        "Running",
        0,
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null);
}
