using System.Text;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class FileRuntimePanelViewModelTests
{
    [Fact]
    public async Task DeferredInitializationDoesNotListUntilExplicitlyStarted()
    {
        var client = new StubFilePanelClient();
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        client.AddProfile("files.remote", "Remote");

        Assert.Equal(0, client.ListCallCount);
        Assert.Equal(2, panel.Profiles.Count);

        await panel.StartInitialization();

        Assert.Equal(1, client.ListCallCount);
        Assert.Equal("/", panel.LocationText);
    }

    [Fact]
    public async Task StartingDeferredInitializationMoreThanOnceReusesTheSameOperation()
    {
        var listing = new TaskCompletionSource<FilePanelResult<FilePanelPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubFilePanelClient
        {
            ListCompletion = listing,
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        var first = panel.StartInitialization();
        var second = panel.StartInitialization();

        Assert.Same(first, second);
        Assert.Same(first, panel.Initialization);
        Assert.Equal(1, client.ListCallCount);

        listing.SetResult(FilePanelResult<FilePanelPage>.Success(
            new FilePanelPage([], null)));
        await first;
    }

    [Fact]
    public async Task DisposingDeferredPanelBeforeStartNeverLists()
    {
        var client = new StubFilePanelClient();
        var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        panel.Dispose();

        Assert.Equal(0, client.ListCallCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => panel.StartInitialization());
        Assert.Equal(0, client.ListCallCount);
    }

    [Fact]
    public async Task LoadingAndEmptyLocationHaveDistinctAccessiblePresentation()
    {
        var listing = new TaskCompletionSource<FilePanelResult<FilePanelPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubFilePanelClient
        {
            ListCompletion = listing,
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        Assert.Equal(FileBrowserContentState.Loading, panel.ContentState);
        Assert.True(panel.ShowLoadingState);
        Assert.Equal("File Viewer loading", panel.ContentPresentation.AccessibleName);

        listing.SetResult(FilePanelResult<FilePanelPage>.Success(
            new FilePanelPage([], null)));
        await panel.Initialization;

        Assert.Equal(FileBrowserContentState.EmptyLocation, panel.ContentState);
        Assert.True(panel.ShowEmptyLocationState);
        Assert.False(panel.ShowSearchNoResultsState);
        Assert.Equal("File Viewer empty location", panel.ContentPresentation.AccessibleName);
        Assert.Contains(
            "Create a folder",
            panel.ContentPresentation.SuggestedAction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializationListsProviderRootAndAppliesNameFilter()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "zeta.txt", FilePanelEntryKind.File, 12));
        client.Entries.Add(Entry(client.Root, "alpha", FilePanelEntryKind.Directory, null));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal("Home", panel.SelectedProfile?.Name);
        Assert.Equal("/", panel.LocationText);
        Assert.Collection(
            panel.Entries,
            item => Assert.Equal("alpha", item.Name),
            item => Assert.Equal("zeta.txt", item.Name));
        Assert.Equal("2 item(s)", panel.Status);

        panel.Filter = "zeta";

        var visible = Assert.Single(panel.Entries);
        Assert.Equal("zeta.txt", visible.Name);
        Assert.Equal("1 of 2 item(s)", panel.Status);

        panel.Filter = "missing";

        Assert.Empty(panel.Entries);
        Assert.Equal("0 of 2 item(s)", panel.Status);
        Assert.Equal(FileBrowserContentState.SearchNoResults, panel.ContentState);
        Assert.True(panel.ShowSearchNoResultsState);
        Assert.Equal(
            "File Viewer search has no results",
            panel.ContentPresentation.AccessibleName);
        Assert.Contains("missing", panel.ContentPresentation.Message, StringComparison.Ordinal);

        panel.Filter = string.Empty;

        Assert.Equal(2, panel.Entries.Count);
        Assert.Equal("2 item(s)", panel.Status);
        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
    }

    [Fact]
    public async Task UnsavedViewerDefaultsToBuiltInHomeEvenWhenAnotherProfileSortsFirst()
    {
        var client = new StubFilePanelClient();
        client.AddProfile("files.alpha", "AAA remote", prepend: true);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal("builtin.files.home", panel.SelectedProfile?.Id);
    }

    [Fact]
    public async Task FileSelectionUsesBoundedPreviewAndFormatsText()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "readme.txt", FilePanelEntryKind.File, 5));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("readme.txt")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("hello"),
            isTruncated: false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        var selected = Assert.Single(panel.Entries);

        panel.SelectedEntry = selected;
        await panel.PreviewSelectedAsync();

        Assert.Equal("hello", panel.PreviewText);
        Assert.Equal("readme.txt", panel.PreviewTitle);
        Assert.NotNull(client.LastPreviewRequest);
        Assert.InRange(client.LastPreviewRequest!.MaximumBytes, 1, 256 * 1024);
    }

    [Fact]
    public async Task VersionedDeleteUsesOptimisticPrecondition()
    {
        var client = new StubFilePanelClient();
        var location = client.Root
            .Child(new FilePanelPathSegment("stale.txt"))
            .WithVersion("etag-7");
        client.Entries.Add(new FilePanelEntry(
            location,
            "stale.txt",
            FilePanelEntryKind.File,
            8,
            null,
            false));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);

        var deleted = await panel.DeleteSelectedAsync();

        Assert.True(deleted);
        Assert.NotNull(client.LastDeleteRequest);
        Assert.Equal(
            FilePanelMutationPreconditionKind.VersionMatches,
            client.LastDeleteRequest!.Precondition.Kind);
        Assert.Equal("etag-7", client.LastDeleteRequest.Precondition.Version);
    }

    [Fact]
    public async Task TypedProviderFailureIsVisibleAndRetryableThroughRefresh()
    {
        var client = new StubFilePanelClient
        {
            ListError = new FilePanelError(
                FilePanelErrorCode.AccessDenied,
                "file_access_denied",
                "Permission denied by provider.",
                false),
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.True(panel.HasError);
        Assert.Equal("Permission denied by provider.", panel.ErrorMessage);
        Assert.Equal("Location unavailable", panel.Status);

        client.ListError = null;
        await panel.RefreshAsync();

        Assert.False(panel.HasError);
        Assert.Equal("This location is empty", panel.Status);
    }

    [Theory]
    [InlineData(
        FilePanelErrorCode.AccessDenied,
        FileBrowserContentState.AccessRequired,
        "File Viewer access required")]
    [InlineData(
        FilePanelErrorCode.UnsupportedCapability,
        FileBrowserContentState.Unsupported,
        "File Viewer location unsupported")]
    public async Task PermissionAndUnsupportedFailuresHaveDistinctPresentation(
        FilePanelErrorCode errorCode,
        FileBrowserContentState expectedState,
        string accessibleName)
    {
        var client = new StubFilePanelClient
        {
            ListError = new FilePanelError(
                errorCode,
                $"test_{errorCode}",
                "Provider-specific detail.",
                Retryable: false),
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal(expectedState, panel.ContentState);
        Assert.True(panel.ShowErrorState);
        Assert.Equal(accessibleName, panel.ContentPresentation.AccessibleName);
        Assert.Equal("Provider-specific detail.", panel.ContentPresentation.Message);
        Assert.False(panel.CanRetryContentState);
        Assert.False(panel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecoverableRefreshClearsStaleSelectionAndPreviewBeforeRetry()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "stale.txt", FilePanelEntryKind.File, 5));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("stale.txt")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("stale preview"),
            isTruncated: false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();
        Assert.True(panel.HasPreview);

        client.ListError = new FilePanelError(
            FilePanelErrorCode.Offline,
            "provider_offline",
            "The provider is temporarily offline.",
            Retryable: true);
        await panel.RefreshAsync();

        Assert.Equal(FileBrowserContentState.RecoverableError, panel.ContentState);
        Assert.Null(panel.SelectedEntry);
        Assert.Null(panel.SelectedMetadata);
        Assert.False(panel.HasPreview);
        Assert.True(panel.CanRetryContentState);
        Assert.True(panel.RetryCommand.CanExecute(null));

        client.ListError = null;
        await panel.RetryAsync();

        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.HasError);
        Assert.False(panel.RetryCommand.CanExecute(null));
        Assert.Single(panel.Entries);
    }

    [Fact]
    public async Task ValidationAndMutationFailuresStayVisibleWithoutReplacingTheListing()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "keep.txt", FilePanelEntryKind.File, 5));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.ReportValidationError("Choose a valid local file.");

        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.ShowErrorState);
        Assert.True(panel.HasOperationIssue);
        Assert.Equal(FileOperationIssueKind.Validation, panel.OperationIssue?.Kind);
        Assert.Single(panel.Entries);

        client.CreateDirectoryError = new FilePanelError(
            FilePanelErrorCode.Conflict,
            "folder_conflict",
            "That folder already exists.",
            Retryable: true);
        var created = await panel.CreateFolderAsync("existing");

        Assert.False(created);
        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.ShowErrorState);
        Assert.Equal(FileOperationIssueKind.Conflict, panel.OperationIssue?.Kind);
        Assert.Null(panel.ContentIssue);
        Assert.Single(panel.Entries);
    }

    [Fact]
    public async Task TransferFailureIsOperationFeedbackRatherThanLocationRetryState()
    {
        var client = new StubFilePanelClient();
        var source = Entry(client.Root, "source.txt", FilePanelEntryKind.File, 5);
        client.Entries.Add(source);
        var transferError = new FilePanelError(
            FilePanelErrorCode.Offline,
            "transfer_offline",
            "The transfer service is offline.",
            Retryable: true);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue(transferError));
        await panel.Initialization;
        var destination = client.Root.Child(new FilePanelPathSegment("copy.txt"));

        var queued = await panel.QueueTransferAsync(
            new FilePanelTransferRequest(
                source.Location,
                destination,
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail,
                maximumBytes: 1024));

        Assert.False(queued);
        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.CanRetryContentState);
        Assert.Equal(FileOperationIssueKind.Offline, panel.OperationIssue?.Kind);
        Assert.Null(panel.ContentIssue);
        Assert.Single(panel.Entries);
    }

    [Fact]
    public async Task FailedDestinationNavigationCannotLeaveEntriesFromThePreviousLocation()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "old.txt", FilePanelEntryKind.File, 5));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);
        var remote = client.AddProfile("files.remote", "Remote");
        client.ListError = new FilePanelError(
            FilePanelErrorCode.Offline,
            "remote_offline",
            "The remote provider is offline.",
            Retryable: true);

        await panel.SelectProfileAsync(remote);

        Assert.Equal(remote.Root, panel.CurrentLocation);
        Assert.Empty(panel.Entries);
        Assert.Null(panel.SelectedEntry);
        Assert.Equal(FileBrowserContentState.RecoverableError, panel.ContentState);
        Assert.NotNull(panel.ContentIssue);
        Assert.Null(panel.OperationIssue);
    }

    [Theory]
    [InlineData(FilePanelErrorCode.AccessDenied, FileOperationIssueKind.PermissionDenied)]
    [InlineData(FilePanelErrorCode.AuthenticationRequired, FileOperationIssueKind.AuthenticationRequired)]
    [InlineData(FilePanelErrorCode.Offline, FileOperationIssueKind.Offline)]
    [InlineData(FilePanelErrorCode.QuotaExceeded, FileOperationIssueKind.QuotaExceeded)]
    [InlineData(FilePanelErrorCode.Conflict, FileOperationIssueKind.Conflict)]
    [InlineData(FilePanelErrorCode.PreconditionFailed, FileOperationIssueKind.Stale)]
    [InlineData(FilePanelErrorCode.UnsupportedCapability, FileOperationIssueKind.Unsupported)]
    [InlineData(FilePanelErrorCode.PartialTransfer, FileOperationIssueKind.Partial)]
    [InlineData(FilePanelErrorCode.Cancelled, FileOperationIssueKind.Cancelled)]
    public async Task ProviderFailuresExposeStableUserFacingState(
        FilePanelErrorCode errorCode,
        FileOperationIssueKind expectedKind)
    {
        var client = new StubFilePanelClient
        {
            ListError = new FilePanelError(
                errorCode,
                $"test_{errorCode}",
                "Provider detail.",
                Retryable: false),
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal(expectedKind, panel.CurrentIssue?.Kind);
        Assert.Equal("Provider detail.", panel.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(panel.ErrorTitle));
        Assert.False(string.IsNullOrWhiteSpace(panel.ErrorSuggestedAction));
    }

    [Theory]
    [InlineData(
        FilePanelErrorCode.AlreadyExists,
        "Destination already exists",
        "Keep Both")]
    [InlineData(FilePanelErrorCode.SharingViolation, "File is in use", "process")]
    [InlineData(FilePanelErrorCode.DirectoryNotEmpty, "Folder is not empty", "recursive")]
    public void ConflictFailuresKeepOperationSpecificRecovery(
        FilePanelErrorCode errorCode,
        string title,
        string expectedAction)
    {
        var issue = FileOperationIssue.FromProvider(new FilePanelError(
            errorCode,
            $"test_{errorCode}",
            "Provider detail.",
            Retryable: false));

        Assert.Equal(FileOperationIssueKind.Conflict, issue.Kind);
        Assert.Equal(title, issue.Title);
        Assert.Contains(expectedAction, issue.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        Assert.True(issue.CanRetry);
    }

    [Fact]
    public async Task EntriesCanBeSortedWithoutMovingFoldersBelowFiles()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "folder", FilePanelEntryKind.Directory, null));
        client.Entries.Add(Entry(client.Root, "small.txt", FilePanelEntryKind.File, 4));
        client.Entries.Add(Entry(client.Root, "large.txt", FilePanelEntryKind.File, 4096));
        client.Entries.Add(Entry(client.Root, "unknown.txt", FilePanelEntryKind.File, null));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.SortField = FileEntrySortField.Size;
        panel.SortDirection = FileEntrySortDirection.Descending;
        panel.ViewMode = FileBrowserViewMode.Grid;

        Assert.Equal(
            ["folder", "large.txt", "small.txt", "unknown.txt"],
            panel.Entries.Select(item => item.Name));
        Assert.Equal(FileBrowserViewMode.Grid, panel.ViewMode);

        panel.ChangeSort(FileEntrySortField.Size);

        Assert.Equal(FileEntrySortDirection.Ascending, panel.SortDirection);
        Assert.Equal(
            ["folder", "small.txt", "large.txt", "unknown.txt"],
            panel.Entries.Select(item => item.Name));
    }

    [Fact]
    public async Task SelectionPresentsFreshStatMetadataAlongsidePreview()
    {
        var client = new StubFilePanelClient();
        var listed = Entry(client.Root, "report.bin", FilePanelEntryKind.File, 4);
        client.Entries.Add(listed);
        client.StatEntry = new FilePanelEntry(
            listed.Location.WithVersion("stat-version"),
            listed.Name,
            listed.Kind,
            4096,
            new DateTimeOffset(2026, 7, 22, 12, 30, 0, TimeSpan.Zero),
            false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.Equal(listed.Location, client.LastStatLocation);
        Assert.True(panel.SelectedMetadata?.IsStatBacked);
        Assert.Equal("4 KB", panel.SelectedMetadata?.Size);
        Assert.Equal("stat-version", panel.SelectedMetadata?.Version);
        Assert.Equal("Live provider metadata", panel.SelectedMetadata?.Source);
        Assert.Null(panel.MetadataIssue);
    }

    [Fact]
    public async Task StatFailureKeepsListingMetadataAndExposesTypedIssue()
    {
        var client = new StubFilePanelClient
        {
            StatError = new FilePanelError(
                FilePanelErrorCode.AccessDenied,
                "file_access_denied",
                "Metadata is restricted.",
                Retryable: false),
        };
        client.Entries.Add(Entry(client.Root, "private.txt", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.False(panel.SelectedMetadata?.IsStatBacked);
        Assert.Equal("12 B", panel.SelectedMetadata?.Size);
        Assert.Equal(FileOperationIssueKind.PermissionDenied, panel.MetadataIssue?.Kind);
        Assert.False(panel.HasError);
    }

    [Fact]
    public async Task FilteringOutSelectionClearsPreviewAndMetadata()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "selected.txt", FilePanelEntryKind.File, 12));
        client.Entries.Add(Entry(client.Root, "visible.txt", FilePanelEntryKind.File, 24));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("selected.txt")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("selected preview"),
            isTruncated: false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = panel.Entries.Single(item => item.Name == "selected.txt");
        await panel.PreviewSelectedAsync();

        panel.Filter = "visible";

        Assert.Null(panel.SelectedEntry);
        Assert.Null(panel.SelectedMetadata);
        Assert.False(panel.HasPreview);
        Assert.Equal("Preview", panel.PreviewTitle);
    }

    [Fact]
    public async Task ChangingSelectionCancelsAnInFlightPreviewWithoutLeavingLoadingState()
    {
        var client = new StubFilePanelClient
        {
            PreviewCompletion = new TaskCompletionSource<FilePanelResult<FilePanelPreview>>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        client.Entries.Add(Entry(client.Root, "slow.txt", FilePanelEntryKind.File, 12));
        client.Entries.Add(Entry(client.Root, "folder", FilePanelEntryKind.Directory, null));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = panel.Entries.Single(item => item.Name == "slow.txt");
        var preview = panel.PreviewSelectedAsync();
        Assert.True(panel.IsPreviewLoading);

        panel.SelectedEntry = panel.Entries.Single(item => item.Name == "folder");
        await panel.PreviewSelectedAsync();
        await preview;

        Assert.False(panel.IsPreviewLoading);
        Assert.False(panel.HasPreview);
        Assert.Equal("Preview", panel.PreviewTitle);
    }

    [Fact]
    public async Task ProviderPreviewLimitLargerThanIntDoesNotOverflowBoundedRead()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "files.large-preview",
            "Large preview",
            maximumPreviewBytes: (long)int.MaxValue + 1);
        client.Entries.Add(Entry(remote.Root, "large.txt", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        panel.SelectedEntry = Assert.Single(panel.Entries);

        await panel.PreviewSelectedAsync();

        Assert.Equal(256 * 1024, client.LastPreviewRequest?.MaximumBytes);
    }

    [Fact]
    public async Task TransferAndExternalOpenCapabilitiesFollowProviderAndSelection()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "local.txt", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;

        Assert.False(panel.CanDownload);
        Assert.False(panel.CanUpload);
        Assert.False(panel.CanOpenExternally);

        panel.SelectedEntry = Assert.Single(panel.Entries);

        Assert.True(panel.CanDownload);
        Assert.False(panel.CanUpload);
        Assert.True(panel.CanOpenExternally);

        var remote = client.AddProfile(
            "files.remote",
            "Remote",
            capabilities: FilePanelCapability.List | FilePanelCapability.StreamingWrite);
        client.Entries.Clear();
        client.Entries.Add(Entry(remote.Root, "remote.txt", FilePanelEntryKind.File, 24));
        await panel.SelectProfileAsync(remote);
        panel.SelectedEntry = Assert.Single(panel.Entries);

        Assert.True(panel.CanDownload);
        Assert.True(panel.CanUpload);
        Assert.False(panel.CanOpenExternally);
    }

    [Fact]
    public async Task DownloadEditorPrefersBuiltInHomeDestination()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile("files.remote", "Remote");
        var source = Entry(remote.Root, "archive.zip", FilePanelEntryKind.File, 2048);
        client.Entries.Add(source);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        panel.SelectedEntry = Assert.Single(panel.Entries);

        var editor = panel.CreateDownloadEditor();
        var request = editor.CreateRequest();

        Assert.Equal("builtin.files.home", editor.SelectedDestinationProfile.Id);
        Assert.Equal("/archive.zip", editor.Destination);
        Assert.Equal(source.Location, request.Source);
        Assert.Equal("builtin.files.home", request.Destination.ProviderProfileId);
    }

    [Fact]
    public async Task UploadEditorUsesHomeRelativeSourceAndCurrentDestination()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "files.remote",
            "Remote",
            capabilities: FilePanelCapability.List | FilePanelCapability.StreamingWrite);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        panel.LocationText = "/incoming";
        await panel.NavigateFromTextAsync();

        var localPath = Path.Combine(
            HomePath(),
            $".ghostshell-upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "upload payload");
        try
        {
            var editor = panel.CreateUploadEditor(localPath);
            var request = editor.CreateRequest();

            Assert.Equal("builtin.files.home", editor.Source.Location.ProviderProfileId);
            Assert.Equal(Path.GetFileName(localPath), editor.Source.Name);
            Assert.Equal(remote.Id, editor.SelectedDestinationProfile.Id);
            Assert.Equal($"/incoming/{Path.GetFileName(localPath)}", editor.Destination);
            Assert.Equal(editor.Source.Location, request.Source);
            Assert.Equal(remote.Id, request.Destination.ProviderProfileId);
            Assert.Equal(editor.Destination, FileLocationPresentation.Display(request.Destination));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Fact]
    public async Task UploadEditorRejectsPathOutsideBuiltInHomeBeforeReadingIt()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "files.remote",
            "Remote",
            capabilities: FilePanelCapability.List | FilePanelCapability.StreamingWrite);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        var outsideHome = Path.Combine(
            HomePath(),
            "..",
            $"ghostshell-outside-{Guid.NewGuid():N}.txt");

        var error = Assert.Throws<ArgumentException>(() => panel.CreateUploadEditor(outsideHome));

        Assert.Contains("Home folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedBuiltInHomeFileResolvesToLocalPath()
    {
        var client = new StubFilePanelClient();
        var folder = client.Root.Child(new FilePanelPathSegment("documents"));
        client.Entries.Add(Entry(folder, "notes.txt", FilePanelEntryKind.File, 32));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);

        var resolved = panel.GetSelectedLocalPath();

        Assert.True(panel.CanOpenExternally);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(HomePath(), "documents", "notes.txt")),
            resolved);
    }

    [Fact]
    public async Task WindowsShapedProviderSegmentCannotResolveOutsideHome()
    {
        const string windowsTraversal = @"..\outside.txt";
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(
            client.Root,
            windowsTraversal,
            FilePanelEntryKind.File,
            1));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);

        if (OperatingSystem.IsWindows())
        {
            var error = Assert.Throws<InvalidOperationException>(panel.GetSelectedLocalPath);
            Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        // Backslash is a legal filename character on POSIX, not a path separator.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(HomePath(), windowsTraversal)),
            panel.GetSelectedLocalPath());
    }

    [Fact]
    public async Task SavedProviderSelectionWaitsForTheLiveCatalogRefresh()
    {
        var client = new StubFilePanelClient();
        var savedId = new FileProviderProfileId("files.saved");
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            initialProfileId: savedId);

        await panel.Initialization;
        Assert.Null(panel.SelectedProfile);
        Assert.Contains("not currently available", panel.ErrorMessage, StringComparison.Ordinal);

        client.AddProfile("files.saved", "Saved provider");

        Assert.Equal("files.saved", panel.SelectedProfile?.Id);
        Assert.Equal("files.saved", panel.CurrentLocation?.ProviderProfileId);
    }

    private static FilePanelEntry Entry(
        FilePanelLocation parent,
        string name,
        FilePanelEntryKind kind,
        long? size) => new(
        parent.Child(new FilePanelPathSegment(name)).WithVersion($"version-{name}"),
        name,
        kind,
        size,
        new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
        false);

    private static string HomePath()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)
            ? Path.GetFullPath(AppContext.BaseDirectory)
            : Path.GetFullPath(path);
    }

    private sealed class StubFilePanelClient : IFilePanelClient, IFileProviderProfileRuntime
    {
        public StubFilePanelClient()
        {
            Root = new FilePanelLocation(
                "builtin.files.home",
                "local",
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            Profiles =
            [
                new FileProviderProfileDescriptor(
                    "builtin.files.home",
                    "Home",
                    FileProviderFamily.Posix,
                    Root,
                    FilePanelCapability.List
                        | FilePanelCapability.Stat
                        | FilePanelCapability.RangedRead
                        | FilePanelCapability.CreateDirectory
                        | FilePanelCapability.Rename
                        | FilePanelCapability.Delete,
                    500,
                    1024 * 1024),
            ];
        }

        public FilePanelLocation Root { get; }

        public List<FilePanelEntry> Entries { get; } = [];

        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; private set; }

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

        public event EventHandler? ProfilesChanged;

        public FilePanelError? ListError { get; set; }

        public TaskCompletionSource<FilePanelResult<FilePanelPage>>? ListCompletion { get; set; }

        public int ListCallCount { get; private set; }

        public FilePanelError? StatError { get; set; }

        public FilePanelError? CreateDirectoryError { get; set; }

        public FilePanelEntry? StatEntry { get; set; }

        public FilePanelPreview? Preview { get; set; }

        public TaskCompletionSource<FilePanelResult<FilePanelPreview>>? PreviewCompletion { get; set; }

        public FilePanelLocation? LastStatLocation { get; private set; }

        public FilePanelPreviewRequest? LastPreviewRequest { get; private set; }

        public FilePanelDeleteRequest? LastDeleteRequest { get; private set; }

        public FileProviderProfileDescriptor AddProfile(
            string id,
            string name,
            bool prepend = false,
            FilePanelCapability capabilities = FilePanelCapability.List,
            long maximumPreviewBytes = 1024 * 1024)
        {
            var root = new FilePanelLocation(
                id,
                id,
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            var profile = new FileProviderProfileDescriptor(
                id,
                name,
                FileProviderFamily.Posix,
                root,
                capabilities,
                500,
                maximumPreviewBytes);
            Profiles = prepend ? [profile, .. Profiles] : [.. Profiles, profile];
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return profile;
        }

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileProviderTestResult(true, "ok", profile.Name));

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            ListCallCount++;
            if (ListCompletion is not null)
            {
                return new ValueTask<FilePanelResult<FilePanelPage>>(
                    ListCompletion.Task.WaitAsync(cancellationToken));
            }

            return ValueTask.FromResult(ListError is null
                ? FilePanelResult<FilePanelPage>.Success(new FilePanelPage(Entries, null))
                : FilePanelResult<FilePanelPage>.Failure(ListError));
        }

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken)
        {
            LastStatLocation = location;
            return ValueTask.FromResult(StatError is not null
                ? FilePanelResult<FilePanelEntry>.Failure(StatError)
                : FilePanelResult<FilePanelEntry>.Success(StatEntry ?? Entries.Single(
                    item => item.Location == location)));
        }

        public async ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken)
        {
            LastPreviewRequest = request;
            if (PreviewCompletion is not null)
            {
                return await PreviewCompletion.Task.WaitAsync(cancellationToken);
            }

            return FilePanelResult<FilePanelPreview>.Success(
                Preview ?? new FilePanelPreview(
                    request.Location,
                    FilePanelPreviewKind.Text,
                    "text/plain; charset=utf-8",
                    [],
                    false));
        }

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CreateDirectoryError is null
                ? FilePanelResult<FilePanelEntry>.Success(new FilePanelEntry(
                    request.Location,
                    "folder",
                    FilePanelEntryKind.Directory,
                    null,
                    null,
                    false))
                : FilePanelResult<FilePanelEntry>.Failure(CreateDirectoryError));

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(new FilePanelEntry(
                request.Destination,
                "renamed",
                FilePanelEntryKind.File,
                0,
                null,
                false)));

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken)
        {
            LastDeleteRequest = request;
            Entries.RemoveAll(item => item.Location == request.Location);
            return ValueTask.FromResult(FilePanelResult<FilePanelDeleteReceipt>.Success(
                new FilePanelDeleteReceipt(request.Location, false)));
        }

        public void Dispose() => ProfilesChanged = null;
    }

    private sealed class StubTransferQueue(FilePanelError? enqueueError = null)
        : IFileTransferQueueClient
    {
        public IReadOnlyList<FilePanelTransferSnapshot> Transfers => [];

        public event EventHandler? TransfersChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                enqueueError is null
                    ? FilePanelResult<FilePanelTransferSnapshot>.Success(
                        new FilePanelTransferSnapshot(
                            FilePanelTransferId.New(),
                            request,
                            request.Destination,
                            FilePanelTransferState.Queued,
                            "Queued",
                            0,
                            null,
                            null,
                            DateTimeOffset.UnixEpoch,
                            null,
                            null))
                    : FilePanelResult<FilePanelTransferSnapshot>.Failure(enqueueError));

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
