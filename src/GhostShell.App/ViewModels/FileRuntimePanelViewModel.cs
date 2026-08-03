using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class FileEntryViewModel
{
    public FileEntryViewModel(FilePanelEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public FilePanelEntry Entry { get; }

    public string Name => Entry.Name;

    public string Kind => Entry.Kind.ToString();

    public string Size => Entry.Kind == FilePanelEntryKind.Directory
        ? "Folder"
        : FormatSize(Entry.Size);

    public string Modified => Entry.LastModifiedAt?.ToLocalTime().ToString("g") ?? "Unknown";

    public bool IsDirectory => Entry.Kind == FilePanelEntryKind.Directory;

    public bool IsLink => Entry.Kind == FilePanelEntryKind.Link;

    internal static string FormatSize(long? size)
    {
        if (size is null)
        {
            return "Unknown";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)size.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size.Value.ToString(CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

public sealed class FileRuntimePanelViewModel : RuntimePanelViewModel
{
    private const int DefaultPageSize = 250;
    private const int DefaultPreviewBytes = 256 * 1024;
    private const int MaximumFormattedBinaryBytes = 16 * 1024;
    private readonly IFilePanelClient _client;
    private readonly ConnectionProfile _connection;
    private readonly IHostedFilePanelClient? _hostedClient;
    private readonly IFileTransferQueueClient? _transferQueue;
    private readonly IFileProviderProfileRuntime? _profileRuntime;
    private readonly string? _initialProfileId;
    private readonly FilePanelLocation? _initialLocation;
    private readonly string? _initialLocationText;
    private readonly object _initializationGate = new();
    private readonly object _initialSelectionGate = new();
    private readonly AsyncActionCommand _retryCommand;
    private readonly List<FilePanelEntry> _allEntries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _initialization = Task.CompletedTask;
    private Task _initialSelection = Task.CompletedTask;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _preview;
    private CancellationTokenSource? _metadata;
    private FileProviderProfileDescriptor? _selectedProfile;
    private FileEntryViewModel? _selectedEntry;
    private FileEntryMetadataViewModel? _selectedMetadata;
    private FilePanelLocation? _currentLocation;
    private FilePanelLocation? _pendingInitialBindingLocation;
    private string _locationText = string.Empty;
    private string _filter = string.Empty;
    private string? _continuationToken;
    private string _status = "Preparing file provider";
    private FileOperationIssue? _contentIssue;
    private FileOperationIssue? _operationIssue;
    private FileOperationIssue? _metadataIssue;
    private FileOperationIssue? _previewIssue;
    private string? _previewText;
    private string _previewTitle = "Preview";
    private Bitmap? _previewImage;
    private bool _showHidden;
    private bool _isLoading;
    private bool _isPreviewLoading;
    private bool _isMetadataLoading;
    private bool _isPreviewVisible = true;
    private FileEntrySortField _sortField = FileEntrySortField.Name;
    private FileEntrySortDirection _sortDirection = FileEntrySortDirection.Ascending;
    private FileBrowserViewMode _viewMode = FileBrowserViewMode.Details;
    private GridLength _fileNameColumnWidth = new(1, GridUnitType.Star);
    private GridLength _fileSizeColumnWidth = new(90);
    private GridLength _fileModifiedColumnWidth = new(140);
    private bool _initialSelectionPending;
    private bool _initialSelectionRetryRequested;
    private bool _hasLoadedListing;
    private volatile bool _initializationStarted;
    private bool _disposed;

    private readonly IDatabasePanelClient? _databaseClient;
    private readonly IFileContentMaterializer? _materializer;
    private DatabaseRuntimePanelViewModel? _databasePreview;
    private MaterializedFile? _databasePreviewFile;

    public FileRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IFilePanelClient client,
        IFileTransferQueueClient? transferQueue = null,
        FileProviderProfileId? initialProfileId = null,
        FilePanelLocation? initialLocation = null,
        string? initialLocationText = null,
        bool deferInitialization = false,
        ConnectionProfile? connection = null,
        IDatabasePanelClient? databaseClient = null)
        : base(id, PanelKind.FileViewer, title, "Files")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        // Both are optional: a build without database drivers, or a client that
        // cannot hand out a real path, simply previews databases as bytes.
        _databaseClient = databaseClient;
        _materializer = client as IFileContentMaterializer;
        _connection = connection ?? BuiltInConnections.Local;
        _retryCommand = new AsyncActionCommand(
            () => RetryAsync(),
            () => CanRetryContentState);
        _hostedClient = client as IHostedFilePanelClient;
        _transferQueue = transferQueue;
        _profileRuntime = client as IFileProviderProfileRuntime;
        if (initialProfileId is { } profileId
            && initialLocation is not null
            && profileId.Value != initialLocation.ProviderProfileId)
        {
            throw new ArgumentException(
                "The restored file location must belong to the initial provider profile.",
                nameof(initialLocation));
        }

        _initialLocation = initialLocation;
        _initialLocationText = initialLocationText;
        _initialProfileId = initialLocation?.ProviderProfileId ?? initialProfileId?.Value;
        _initialSelectionPending = _initialProfileId is not null;
        Replace(Profiles, client.Profiles);
        if (_hostedClient is not null)
        {
            _hostedClient.ProfilesChanged += OnProfilesChanged;
        }
        else if (_profileRuntime is not null)
        {
            _profileRuntime.ProfilesChanged += OnProfilesChanged;
        }

        if (!deferInitialization)
        {
            _ = StartInitialization();
        }
    }

    public ObservableCollection<FileProviderProfileDescriptor> Profiles { get; } = [];

    public ObservableCollection<FileEntryViewModel> Entries { get; } = [];

    public Task Initialization
    {
        get
        {
            lock (_initializationGate)
            {
                return _initialization;
            }
        }
    }

    public Task StartInitialization()
    {
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializationStarted)
            {
                return _initialization;
            }

            _initializationStarted = true;
            _initialization = InitializeAsync();
            return _initialization;
        }
    }

    public IHostedFilePanelClient? HostedClient => _hostedClient;

    public ConnectionId ConnectionId => _connection.Id;

    public string ConnectionDisplayName =>
        SelectedProfile is null
            ? (_connection.Endpoint is ConnectionEndpoint.Local ? "Local" : _connection.Name)
            : SelectedProfile.Id == BuiltInFileProviders.HomeId.Value
                ? "Local"
                : SelectedProfile.Name;

    public bool UsesConnection(ConnectionId connectionId)
    {
        if (SelectedProfile?.Id == BuiltInFileProviders.HomeId.Value)
        {
            return connectionId == _connection.Id
                && _connection.Endpoint is ConnectionEndpoint.Local;
        }

        return SelectedProfile?.Id == ConnectionFileProviderProfiles.Id(connectionId).Value;
    }

    public bool UsesProfile(FileProviderProfileId profileId) =>
        SelectedProfile?.Id == profileId.Value
        || (SelectedProfile is null && _initialProfileId == profileId.Value);

    public FileProviderProfileDescriptor? SelectedProfile
    {
        get => _selectedProfile;
        private set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(ConnectionDisplayName));
                OnPropertyChanged(nameof(CanCreateFolder));
                OnPropertyChanged(nameof(CanRename));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanTransfer));
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanUpload));
                OnPropertyChanged(nameof(CanOpenExternally));
                OnContentPresentationChanged();
            }
        }
    }

    public FileEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                ClearMetadata();
                _preview?.Cancel();
                IsPreviewLoading = false;
                ClearPreview();
                OnPropertyChanged(nameof(CanRename));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanTransfer));
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanOpenExternally));
            }
        }
    }

    public FileEntryMetadataViewModel? SelectedMetadata
    {
        get => _selectedMetadata;
        private set
        {
            if (SetProperty(ref _selectedMetadata, value))
            {
                OnPropertyChanged(nameof(HasSelectedMetadata));
            }
        }
    }

    public bool HasSelectedMetadata => SelectedMetadata is not null;

    public FileOperationIssue? MetadataIssue
    {
        get => _metadataIssue;
        private set
        {
            if (SetProperty(ref _metadataIssue, value))
            {
                OnPropertyChanged(nameof(HasMetadataIssue));
            }
        }
    }

    public bool HasMetadataIssue => MetadataIssue is not null;

    public FilePanelLocation? CurrentLocation
    {
        get => _currentLocation;
        private set
        {
            if (SetProperty(ref _currentLocation, value))
            {
                LocationText = value is null ? string.Empty : FileLocationPresentation.Display(value);
                OnPropertyChanged(nameof(CanNavigateUp));
                OnPropertyChanged(nameof(CanCreateFolder));
                OnContentPresentationChanged();
            }
        }
    }

    public string LocationText
    {
        get => _locationText;
        set => SetProperty(ref _locationText, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ApplyFilter();
                UpdateListingStatus();
            }
        }
    }

    public FileEntrySortField SortField
    {
        get => _sortField;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }

            if (SetProperty(ref _sortField, value))
            {
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<FileEntrySortField> SortFields { get; } =
        Enum.GetValues<FileEntrySortField>();

    public FileEntrySortDirection SortDirection
    {
        get => _sortDirection;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }

            if (SetProperty(ref _sortDirection, value))
            {
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<FileEntrySortDirection> SortDirections { get; } =
        Enum.GetValues<FileEntrySortDirection>();

    public FileBrowserViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }

            if (SetProperty(ref _viewMode, value))
            {
                OnPropertyChanged(nameof(IsDetailsView));
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(IsGridView));
            }
        }
    }

    public IReadOnlyList<FileBrowserViewMode> ViewModes { get; } =
        Enum.GetValues<FileBrowserViewMode>();

    public bool IsDetailsView => ViewMode == FileBrowserViewMode.Details;

    public bool IsListView => ViewMode == FileBrowserViewMode.List;

    public bool IsGridView => ViewMode == FileBrowserViewMode.Grid;

    public GridLength FileNameColumnWidth
    {
        get => _fileNameColumnWidth;
        set => SetProperty(ref _fileNameColumnWidth, value);
    }

    public GridLength FileSizeColumnWidth
    {
        get => _fileSizeColumnWidth;
        set => SetProperty(ref _fileSizeColumnWidth, value);
    }

    public GridLength FileModifiedColumnWidth
    {
        get => _fileModifiedColumnWidth;
        set => SetProperty(ref _fileModifiedColumnWidth, value);
    }

    public void ChangeSort(FileEntrySortField field)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        if (SortField == field)
        {
            SortDirection = SortDirection == FileEntrySortDirection.Ascending
                ? FileEntrySortDirection.Descending
                : FileEntrySortDirection.Ascending;
            return;
        }

        _ = SetProperty(
            ref _sortDirection,
            FileEntrySortDirection.Ascending,
            nameof(SortDirection));
        _ = SetProperty(ref _sortField, field, nameof(SortField));
        ApplyFilter();
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (SetProperty(ref _showHidden, value) && CurrentLocation is not null)
            {
                _ = RefreshAsync();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                NotifyFileInteractionStateChanged();
                OnPropertyChanged(nameof(ShowEmptyState));
                OnContentPresentationChanged();
            }
        }
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set
        {
            if (SetProperty(ref _isPreviewLoading, value))
            {
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
            }
        }
    }

    public bool IsMetadataLoading
    {
        get => _isMetadataLoading;
        private set => SetProperty(ref _isMetadataLoading, value);
    }

    /// <summary>
    /// Whether the optional inspector occupies layout space. Preview content is
    /// retained while hidden, so reopening the panel does not repeat a remote read.
    /// </summary>
    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set
        {
            if (SetProperty(ref _isPreviewVisible, value))
            {
                OnPropertyChanged(nameof(PreviewVisibilityStatus));
            }
        }
    }

    public string PreviewVisibilityStatus =>
        IsPreviewVisible ? "Preview visible" : "Preview hidden";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public FileOperationIssue? ContentIssue
    {
        get => _contentIssue;
        private set
        {
            if (SetProperty(ref _contentIssue, value))
            {
                PublishIssueState();
                OnContentPresentationChanged();
            }
        }
    }

    public FileOperationIssue? OperationIssue
    {
        get => _operationIssue;
        private set
        {
            if (SetProperty(ref _operationIssue, value))
            {
                OnPropertyChanged(nameof(HasOperationIssue));
                PublishIssueState();
            }
        }
    }

    public FileOperationIssue? CurrentIssue => OperationIssue ?? ContentIssue;

    public string? ErrorMessage => CurrentIssue?.Message;

    public string ErrorTitle => CurrentIssue?.Title ?? "File operation failed";

    public string? ErrorSuggestedAction => CurrentIssue?.SuggestedAction;

    public bool CanRetryError => CurrentIssue?.CanRetry == true;

    public bool HasOperationIssue => OperationIssue is not null;

    public FileOperationIssue? PreviewIssue
    {
        get => _previewIssue;
        private set
        {
            if (SetProperty(ref _previewIssue, value))
            {
                OnPropertyChanged(nameof(HasPreviewIssue));
            }
        }
    }

    public bool HasPreviewIssue => PreviewIssue is not null;

    public string PreviewTitle
    {
        get => _previewTitle;
        private set => SetProperty(ref _previewTitle, value);
    }

    public string? PreviewText
    {
        get => _previewText;
        private set
        {
            if (SetProperty(ref _previewText, value))
            {
                OnPropertyChanged(nameof(HasTextPreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
            }
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            var previous = _previewImage;
            if (SetProperty(ref _previewImage, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasImagePreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
            }
        }
    }

    public bool HasError => CurrentIssue is not null;

    /// <summary>
    /// The database viewer bound to the selected file, when that file is a
    /// database. It is the same view model the docked database panel uses, so
    /// the preview is the product's database viewer rather than a second one.
    /// </summary>
    public DatabaseRuntimePanelViewModel? DatabasePreview => _databasePreview;

    public bool HasDatabasePreview => _databasePreview is not null;

    public bool HasTextPreview => !string.IsNullOrEmpty(PreviewText);

    public bool HasImagePreview => PreviewImage is not null;

    public bool HasPreview => HasTextPreview || HasImagePreview || HasDatabasePreview;

    public bool ShowPreviewPlaceholder => !HasPreview && !IsPreviewLoading;

    public FileBrowserContentPresentation ContentPresentation =>
        FileBrowserContentPresentation.Resolve(
            IsLoading,
            _hasLoadedListing,
            _allEntries.Count,
            Entries.Count,
            Filter,
            ContentIssue,
            ContentIssue?.Message,
            CanCreateFolder,
            CurrentLocation is not null);

    public FileBrowserContentState ContentState => ContentPresentation.State;

    public bool ShowLoadingState =>
        ContentState == FileBrowserContentState.Loading && !_hasLoadedListing;

    public bool ShowNavigationProgress => IsLoading && _hasLoadedListing;

    public bool ShowEmptyLocationState =>
        ContentState == FileBrowserContentState.EmptyLocation;

    public bool ShowSearchNoResultsState =>
        ContentState == FileBrowserContentState.SearchNoResults;

    public bool ShowErrorState => ContentPresentation.IsError;

    public bool CanRetryContentState => !_disposed && ContentPresentation.CanRetry;

    public bool ShowEmptyState => ShowEmptyLocationState || ShowSearchNoResultsState;

    public ICommand RetryCommand => _retryCommand;

    public bool CanSelectProfile => !IsLoading && !IsInitialHostedBindingPending;

    public bool CanEditLocation => !IsLoading && !IsInitialHostedBindingPending;

    public bool CanNavigateUp => !IsLoading
        && !IsInitialHostedBindingPending
        && CurrentLocation?.Address is FilePanelAddress.Hierarchical hierarchical
        && !hierarchical.Path.IsRoot;

    public bool HasMore => !string.IsNullOrWhiteSpace(_continuationToken);

    public bool HasListingSummary => _hasLoadedListing;

    public bool CanCreateFolder => !IsLoading
        && !IsInitialHostedBindingPending
        && SelectedProfile?.Capabilities.HasFlag(
        FilePanelCapability.CreateDirectory) == true
        && CurrentLocation?.Address is FilePanelAddress.Hierarchical;

    public bool CanRename => !IsLoading
        && SelectedEntry is not null
        && SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Rename) == true;

    public bool CanDelete => !IsLoading
        && SelectedEntry is not null
        && SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Delete) == true;

    public bool CanTransfer => !IsLoading
        && _transferQueue is not null
        && SelectedEntry?.Entry.Kind is
            FilePanelEntryKind.File or FilePanelEntryKind.Directory;

    public bool CanDownload => CanTransfer
        && Profiles.Any(profile => profile.Id == "builtin.files.home");

    public bool CanUpload => !IsLoading
        && !IsInitialHostedBindingPending
        && _transferQueue is not null
        && CurrentLocation is not null
        && SelectedProfile?.Id != BuiltInFileProviders.HomeId.Value
        && SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.StreamingWrite) == true
        && Profiles.Any(profile => profile.Id == "builtin.files.home");

    public bool CanOpenExternally => !IsLoading
        && SelectedProfile?.Id == "builtin.files.home"
        && SelectedEntry?.Entry.Kind == FilePanelEntryKind.File;

    private bool IsInitialHostedBindingPending =>
        _initialSelectionPending
        && _hostedClient is { IsInitialized: false };

    public async Task SelectProfileAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Profiles.Any(item => item.Id == profile.Id))
        {
            SetOperationIssue(FileOperationIssue.Configuration(
                "The selected file-provider profile no longer exists."));
            return;
        }

        if (_initialSelectionPending)
        {
            if (_initialProfileId is not null
                && profile.Id != _initialProfileId)
            {
                SetOperationIssue(FileOperationIssue.Configuration(
                    "This File Viewer is still waiting for its saved provider. "
                    + "It will not substitute another provider before the saved session binds."));
                return;
            }

            await SelectInitialProfileAsync(profile, cancellationToken);
            return;
        }

        SelectedProfile = profile;
        await NavigateAsync(profile.Root, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        CurrentLocation is null
            ? Task.CompletedTask
            : NavigateAsync(CurrentLocation, cancellationToken);

    public Task RetryAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    public Task NavigateUpAsync(CancellationToken cancellationToken = default) =>
        CanNavigateUp && CurrentLocation is not null
            ? NavigateAsync(CurrentLocation.Parent, cancellationToken)
            : Task.CompletedTask;

    public async Task NavigateFromTextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearOperationIssue();
        if (SelectedProfile is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "Choose a file-provider profile first."));
            return;
        }

        FilePanelLocation location;
        try
        {
            location = FileLocationPresentation.Parse(SelectedProfile, LocationText);
        }
        catch (ArgumentException exception)
        {
            SetOperationIssue(FileOperationIssue.Validation(exception.Message));
            return;
        }

        await NavigateAsync(location, cancellationToken);
    }

    public async Task OpenEntryAsync(
        FileEntryViewModel entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.Entry.Location.WithVersion(null), cancellationToken);
        }
        else
        {
            SelectedEntry = entry;
            await PreviewSelectedAsync(cancellationToken);
        }
    }

    public Task PreviewSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedEntry;
        return Task.WhenAll(
            LoadMetadataAsync(selected, cancellationToken),
            LoadPreviewAsync(selected, cancellationToken));
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentLocation is null || string.IsNullOrWhiteSpace(_continuationToken))
        {
            return;
        }

        await LoadPageAsync(CurrentLocation, append: true, cancellationToken);
    }

    public async Task<bool> CreateFolderAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ClearOperationIssue();
        if (!CanCreateFolder || CurrentLocation is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "This provider cannot create a folder at the current location."));
            return false;
        }

        FilePanelLocation location;
        try
        {
            location = CurrentLocation.Child(new FilePanelPathSegment(name.Trim()));
        }
        catch (ArgumentException exception)
        {
            SetOperationIssue(FileOperationIssue.Validation(exception.Message));
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _client.CreateDirectoryAsync(
            new FilePanelCreateDirectoryRequest(
                location,
                FilePanelMutationPrecondition.MustNotExist),
            linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        await RefreshAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RenameSelectedAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ClearOperationIssue();
        if (!CanRename || SelectedEntry is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "Choose an item that this provider can rename."));
            return false;
        }

        FilePanelLocation destination;
        try
        {
            destination = SelectedEntry.Entry.Location.Parent.Child(
                new FilePanelPathSegment(name.Trim()));
        }
        catch (ArgumentException exception)
        {
            SetOperationIssue(FileOperationIssue.Validation(exception.Message));
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _client.RenameAsync(
            new FilePanelRenameRequest(
                SelectedEntry.Entry.Location,
                destination,
                FilePanelMutationPrecondition.MustNotExist),
            linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        await RefreshAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearOperationIssue();
        if (!CanDelete || SelectedEntry is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "Choose an item that this provider can delete."));
            return false;
        }

        var selected = SelectedEntry;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _client.DeleteAsync(
            new FilePanelDeleteRequest(
                selected.Entry.Location,
                Recursive: false,
                selected.Entry.Location.Version is { } version
                    ? FilePanelMutationPrecondition.VersionMatches(version)
                    : FilePanelMutationPrecondition.MustExist),
            linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        SelectedEntry = null;
        await RefreshAsync(cancellationToken);
        return true;
    }

    public FileTransferEditorViewModel CreateTransferEditor()
    {
        if (!CanTransfer || SelectedEntry is null)
        {
            throw new InvalidOperationException(
                "Choose a file or folder before creating a transfer.");
        }

        return new FileTransferEditorViewModel(
            SelectedEntry.Entry,
            Profiles,
            SelectedProfile?.Id);
    }

    public FilePanelTransferRequest CreateIncomingTransferRequest(
        FilePanelEntry source,
        FilePanelTransferOperation operation,
        FilePanelLocation? destinationFolder = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        if (IsLoading || CurrentLocation is null || SelectedProfile is null)
        {
            throw new InvalidOperationException(
                "Wait for the destination folder to finish loading.");
        }

        if (source.Kind is not (
            FilePanelEntryKind.File or FilePanelEntryKind.Directory))
        {
            throw new InvalidOperationException(
                "Only regular files and folders can be transferred.");
        }

        var requiredCapabilities = source.Kind == FilePanelEntryKind.Directory
            ? FilePanelCapability.CreateDirectory | FilePanelCapability.StreamingWrite
            : FilePanelCapability.StreamingWrite;
        if (!SelectedProfile.Capabilities.HasFlag(requiredCapabilities))
        {
            throw new InvalidOperationException(
                "The selected destination cannot receive this item type.");
        }

        var destinationParent = destinationFolder?.WithVersion(null)
            ?? CurrentLocation;
        if (!string.Equals(
                destinationParent.ProviderProfileId,
                SelectedProfile.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                destinationParent.Authority,
                CurrentLocation.Authority,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The drop folder does not belong to the selected destination.");
        }

        var destination = FileLocationPresentation.Child(
            destinationParent,
            source.Name);
        if (source.Location.WithVersion(null) == destination.WithVersion(null))
        {
            throw new InvalidOperationException(
                "The item is already in this destination.");
        }

        if (source.Kind == FilePanelEntryKind.Directory
            && source.Location.WithVersion(null) == destinationParent)
        {
            throw new InvalidOperationException(
                "A folder cannot be transferred into itself.");
        }

        return new FilePanelTransferRequest(
            source.Location,
            destination,
            operation,
            FilePanelConflictPolicy.KeepBoth);
    }

    public bool CanReceiveTransfer(
        FilePanelEntry source,
        FilePanelLocation? destinationFolder = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsLoading || CurrentLocation is null || SelectedProfile is null)
        {
            return false;
        }

        var requiredCapabilities = source.Kind switch
        {
            FilePanelEntryKind.File =>
                FilePanelCapability.StreamingWrite,
            FilePanelEntryKind.Directory =>
                FilePanelCapability.CreateDirectory
                | FilePanelCapability.StreamingWrite,
            _ => FilePanelCapability.None,
        };
        if (requiredCapabilities == FilePanelCapability.None
            || !SelectedProfile.Capabilities.HasFlag(requiredCapabilities))
        {
            return false;
        }

        var destinationParent = destinationFolder?.WithVersion(null)
            ?? CurrentLocation;
        if (!string.Equals(
                destinationParent.ProviderProfileId,
                SelectedProfile.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                destinationParent.Authority,
                CurrentLocation.Authority,
                StringComparison.Ordinal))
        {
            return false;
        }

        var sourceLocation = source.Location.WithVersion(null);
        var destination = FileLocationPresentation.Child(
            destinationParent,
            source.Name);
        return sourceLocation != destination.WithVersion(null)
            && (source.Kind != FilePanelEntryKind.Directory
                || sourceLocation != destinationParent);
    }

    public FileTransferEditorViewModel CreateDownloadEditor()
    {
        if (!CanDownload)
        {
            throw new InvalidOperationException(
                "Choose a regular file before creating a download.");
        }

        return new FileTransferEditorViewModel(
            SelectedEntry!.Entry,
            Profiles,
            "builtin.files.home");
    }

    public FileTransferEditorViewModel CreateUploadEditor(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!CanUpload || CurrentLocation is null || SelectedProfile is null)
        {
            throw new InvalidOperationException(
                "This provider cannot receive an uploaded file at the current location.");
        }

        var homeProfile = Profiles.Single(profile => profile.Id == "builtin.files.home");
        var sourcePath = Path.GetFullPath(localPath.Trim());
        var homePath = BuiltInHomePath();
        if (!IsWithinDirectory(homePath, sourcePath))
        {
            throw new ArgumentException(
                "Choose a file inside your Home folder so the local provider can read it safely.",
                nameof(localPath));
        }

        var relativePath = Path.GetRelativePath(homePath, sourcePath);
        var file = new FileInfo(sourcePath);
        if (!file.Exists)
        {
            throw new ArgumentException("The selected local file no longer exists.", nameof(localPath));
        }

        var segments = relativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => new FilePanelPathSegment(segment));
        var source = new FilePanelEntry(
            new FilePanelLocation(
                homeProfile.Id,
                homeProfile.Root.Authority,
                new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(segments))),
            file.Name,
            FilePanelEntryKind.File,
            file.Length,
            file.LastWriteTimeUtc,
            file.Name.StartsWith(".", StringComparison.Ordinal));
        var editor = new FileTransferEditorViewModel(source, Profiles, SelectedProfile.Id)
        {
            Destination = ChildLocationDisplay(CurrentLocation, file.Name),
        };
        return editor;
    }

    public string GetSelectedLocalPath()
    {
        if (!CanOpenExternally
            || SelectedEntry?.Entry.Location.Address is not FilePanelAddress.Hierarchical path)
        {
            throw new InvalidOperationException(
                "Only regular files from the built-in Home provider can be opened externally.");
        }

        var segments = path.Path.Segments.Select(segment => segment.Value).ToArray();
        var homePath = BuiltInHomePath();
        var resolvedPath = Path.GetFullPath(Path.Combine([homePath, .. segments]));
        if (!IsWithinDirectory(homePath, resolvedPath))
        {
            throw new InvalidOperationException(
                "The selected file resolves outside the built-in Home provider.");
        }

        return resolvedPath;
    }

    public async Task<bool> QueueTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearOperationIssue();
        if (_transferQueue is null)
        {
            SetOperationIssue(FileOperationIssue.Configuration(
                "The transfer queue is unavailable."));
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _transferQueue.EnqueueAsync(request, linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        Status = result.Value!.State == FilePanelTransferState.Skipped
            ? "Transfer skipped because the destination exists"
            : "Transfer queued";
        return true;
    }

    public void ClearError()
    {
        ContentIssue = null;
        OperationIssue = null;
    }

    public void ClearOperationIssue() => OperationIssue = null;

    public void ReportValidationError(string message) =>
        SetOperationIssue(FileOperationIssue.Validation(message));

    public override void Dispose()
    {
        lock (_initializationGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_hostedClient is not null)
        {
            _hostedClient.ProfilesChanged -= OnProfilesChanged;
        }
        else if (_profileRuntime is not null)
        {
            _profileRuntime.ProfilesChanged -= OnProfilesChanged;
        }
        _lifetime.Cancel();
        _navigation?.Cancel();
        _preview?.Cancel();
        _metadata?.Cancel();
        _navigation?.Dispose();
        _preview?.Dispose();
        _metadata?.Dispose();
        _lifetime.Dispose();
        PreviewImage = null;
        ClearDatabasePreview();
        if (_hostedClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async Task InitializeAsync()
    {
        if (Profiles.Count == 0)
        {
            Status = "No file providers configured";
            SetContentIssue(FileOperationIssue.Configuration(
                "Add a file-provider profile before opening the File Viewer."));
            return;
        }

        var initial = _initialProfileId is null
            ? Profiles.FirstOrDefault(item => item.Id == "builtin.files.home")
            : Profiles.FirstOrDefault(item => item.Id == _initialProfileId);
        if (_initialSelectionPending && initial is null)
        {
            Status = "Waiting for saved file provider";
            SetContentIssue(FileOperationIssue.Configuration(
                "The saved File Viewer provider is not currently available."));
            return;
        }

        await SelectInitialProfileAsync(initial ?? Profiles[0]);
    }

    private void OnProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
            || Avalonia.Application.Current is null)
        {
            ApplyProfiles();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyProfiles);
        }
    }

    private void ApplyProfiles()
    {
        if (_disposed)
        {
            return;
        }

        var selectedId = _initialSelectionPending
            ? _initialProfileId
            : SelectedProfile?.Id;
        var previousRoot = SelectedProfile?.Root;
        Replace(Profiles, _client.Profiles);
        if (!_initializationStarted)
        {
            return;
        }

        var selected = selectedId is null
            ? null
            : Profiles.FirstOrDefault(item => item.Id == selectedId);
        if (selected is not null)
        {
            if (_initialSelectionPending || previousRoot != selected.Root)
            {
                _ = _initialSelectionPending
                    ? SelectInitialProfileAsync(selected)
                    : SelectProfileAsync(selected);
            }
            else
            {
                SelectedProfile = selected;
            }
            return;
        }

        if (_initialSelectionPending)
        {
            ResetListing();
            SelectedProfile = null;
            CurrentLocation = null;
            Status = "Saved file provider unavailable";
            SetContentIssue(FileOperationIssue.Configuration(
                "The saved File Viewer provider is not currently available. Repair the saved screen or provider profile."));
            return;
        }

        if (Profiles.Count == 0)
        {
            ResetListing();
            SelectedProfile = null;
            CurrentLocation = null;
            Status = "No file providers configured";
            SetContentIssue(FileOperationIssue.Configuration(
                "Add a file-provider profile before opening the File Viewer."));
            return;
        }

        _ = SelectProfileAsync(Profiles[0]);
    }

    private Task SelectInitialProfileAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        lock (_initialSelectionGate)
        {
            if (!_initialSelection.IsCompleted)
            {
                _initialSelectionRetryRequested = true;
                return _initialSelection;
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _initialSelection = completion.Task;
        }

        _ = CompleteInitialProfileSelectionAsync(
            profile,
            cancellationToken,
            completion);
        return completion.Task;
    }

    private async Task CompleteInitialProfileSelectionAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await SelectInitialProfileCoreAsync(profile, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        FileProviderProfileDescriptor? retryProfile = null;
        lock (_initialSelectionGate)
        {
            if (_initialSelectionRetryRequested
                && _initialSelectionPending
                && !_disposed
                && _initialProfileId is { } initialProfileId)
            {
                retryProfile = Profiles.FirstOrDefault(
                    item => item.Id == initialProfileId);
            }

            _initialSelectionRetryRequested = false;
        }

        if (failure is null)
        {
            completion.TrySetResult();
        }
        else if (failure is OperationCanceledException
                 && cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        else
        {
            completion.TrySetException(failure);
        }

        if (retryProfile is not null)
        {
            _ = SelectInitialProfileAsync(retryProfile);
        }
    }

    private async Task SelectInitialProfileCoreAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken)
    {
        FilePanelLocation initialLocation;
        if (_initialLocation is not null)
        {
            initialLocation = _initialLocation;
        }
        else if (_initialLocationText is null)
        {
            initialLocation = profile.Root;
        }
        else
        {
            try
            {
                initialLocation = FileLocationPresentation.Parse(
                    profile,
                    _initialLocationText);
            }
            catch (ArgumentException)
            {
                SelectedProfile = profile;
                CurrentLocation = null;
                Status = "Saved startup location invalid";
                SetContentIssue(FileOperationIssue.Configuration(
                    "The saved File Viewer startup location is not valid for this provider. Repair the saved screen before reopening it."));
                return;
            }
        }

        _pendingInitialBindingLocation = initialLocation.WithVersion(null);
        await NavigateAsync(initialLocation, cancellationToken);
    }

    private async Task NavigateAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedLocation = location.WithVersion(null);
        if (IsInitialHostedBindingPending
            && normalizedLocation != _pendingInitialBindingLocation)
        {
            LocationText = CurrentLocation is null
                ? string.Empty
                : FileLocationPresentation.Display(CurrentLocation);
            SetOperationIssue(FileOperationIssue.Configuration(
                "This File Viewer must bind its exact saved startup location before navigating elsewhere."));
            return;
        }

        var profile = Profiles.SingleOrDefault(item => item.Id == location.ProviderProfileId);
        if (profile is null)
        {
            SetOperationIssue(FileOperationIssue.Configuration(
                "The selected file-provider profile no longer exists."));
            return;
        }

        SelectedProfile = profile;
        CurrentLocation = normalizedLocation;
        _continuationToken = null;
        SelectedEntry = null;
        ClearPreview();
        await LoadPageAsync(CurrentLocation, append: false, cancellationToken);
        if (_initialSelectionPending
            && location.ProviderProfileId == _initialProfileId
            && (_hostedClient is null || _hostedClient.IsInitialized))
        {
            _initialSelectionPending = false;
            _pendingInitialBindingLocation = null;
            NotifyInitialBindingStateChanged();
        }
    }

    private async Task LoadPageAsync(
        FilePanelLocation location,
        bool append,
        CancellationToken cancellationToken)
    {
        var operation = ReplaceNavigation(cancellationToken);
        IsLoading = true;
        ClearError();
        Status = append ? "Loading more items…" : "Loading folder…";
        var pageSize = Math.Min(DefaultPageSize, SelectedProfile?.MaximumPageSize ?? DefaultPageSize);
        FilePanelResult<FilePanelPage> result;
        try
        {
            result = await _client.ListAsync(
                new FilePanelListRequest(
                    location,
                    pageSize,
                    append ? _continuationToken : null,
                    ShowHidden),
                operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (!operation.IsCancellationRequested)
            {
                if (!append)
                {
                    ResetListing();
                }

                SetContentIssue(FileOperationIssue.Unexpected(
                    "The file provider failed unexpectedly while listing this location."));
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_navigation, operation))
            {
                IsLoading = false;
            }
        }

        if (!ReferenceEquals(_navigation, operation) || operation.IsCancellationRequested)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            if (!append)
            {
                ResetListing();
            }

            Status = "Location unavailable";
            SetContentIssue(FileOperationIssue.FromProvider(result.Error!));
            return;
        }

        if (!append)
        {
            // Preserve the current folder until the replacement has arrived. Clearing and
            // repopulating now happens in one UI turn, avoiding a blank loading frame.
            ResetListing();
        }

        _allEntries.AddRange(result.Value!.Entries);
        _continuationToken = result.Value.ContinuationToken;
        _hasLoadedListing = true;
        OnPropertyChanged(nameof(HasListingSummary));
        ApplyFilter();
        UpdateListingStatus();
        OnPropertyChanged(nameof(HasMore));
    }

    private void NotifyInitialBindingStateChanged()
    {
        NotifyFileInteractionStateChanged();
    }

    private void NotifyFileInteractionStateChanged()
    {
        OnPropertyChanged(nameof(CanSelectProfile));
        OnPropertyChanged(nameof(CanEditLocation));
        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanTransfer));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanOpenExternally));
    }

    private async Task LoadMetadataAsync(
        FileEntryViewModel? entry,
        CancellationToken cancellationToken)
    {
        _metadata?.Cancel();
        _metadata?.Dispose();
        _metadata = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var operation = _metadata;
        SelectedMetadata = null;
        MetadataIssue = null;
        if (entry is null)
        {
            return;
        }

        if (SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Stat) != true)
        {
            SelectedMetadata = new FileEntryMetadataViewModel(
                entry.Entry,
                isStatBacked: false);
            return;
        }

        IsMetadataLoading = true;
        FilePanelResult<FilePanelEntry> result;
        try
        {
            result = await _client.StatAsync(entry.Entry.Location, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (ReferenceEquals(_metadata, operation))
            {
                SelectedMetadata = new FileEntryMetadataViewModel(
                    entry.Entry,
                    isStatBacked: false);
                MetadataIssue = FileOperationIssue.Unexpected(
                    "The provider failed unexpectedly while reading item metadata.");
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_metadata, operation))
            {
                IsMetadataLoading = false;
            }
        }

        if (!ReferenceEquals(_metadata, operation) || operation.IsCancellationRequested)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            SelectedMetadata = new FileEntryMetadataViewModel(
                entry.Entry,
                isStatBacked: false);
            MetadataIssue = FileOperationIssue.FromProvider(result.Error!);
            return;
        }

        SelectedMetadata = new FileEntryMetadataViewModel(
            result.Value!,
            isStatBacked: true);
    }

    private async Task LoadPreviewAsync(
        FileEntryViewModel? entry,
        CancellationToken cancellationToken = default)
    {
        _preview?.Cancel();
        _preview?.Dispose();
        _preview = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var operation = _preview;
        ClearPreview();
        IsPreviewLoading = false;
        if (entry is null || entry.IsDirectory || entry.IsLink)
        {
            return;
        }

        IsPreviewLoading = true;
        PreviewTitle = entry.Name;
        var maximum = Math.Min(
            DefaultPreviewBytes,
            SelectedProfile?.MaximumPreviewBytes ?? DefaultPreviewBytes);
        FilePanelResult<FilePanelPreview> result;
        try
        {
            result = await _client.PreviewAsync(
                new FilePanelPreviewRequest(entry.Entry.Location, maximum),
                operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = "Preview failed unexpectedly.";
                PreviewIssue = FileOperationIssue.Unexpected(PreviewText);
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
            }
        }

        if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            PreviewText = error.Message;
            PreviewIssue = FileOperationIssue.FromProvider(error);
            return;
        }

        PresentPreview(result.Value!);
    }

    private void PresentPreview(FilePanelPreview preview)
    {
        switch (preview.Kind)
        {
            case FilePanelPreviewKind.Image:
                try
                {
                    using (var stream = new MemoryStream(preview.Content.ToArray(), writable: false))
                    {
                        PreviewImage = new Bitmap(stream);
                    }

                    PreviewText = preview.IsTruncated ? "Image preview was truncated." : null;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
                {
                    PreviewText = "The image data could not be decoded safely.";
                }

                break;
            case FilePanelPreviewKind.StructuredText:
                PreviewText = FormatJson(preview.Content.Span, preview.IsTruncated);
                break;
            case FilePanelPreviewKind.Text:
                PreviewText = Encoding.UTF8.GetString(preview.Content.Span)
                    + (preview.IsTruncated ? "\n\n[preview truncated]" : string.Empty);
                break;
            case FilePanelPreviewKind.Database:
                _ = OpenDatabasePreviewAsync(preview.Location);
                break;
            case FilePanelPreviewKind.Hex:
                PreviewText = FormatHex(preview.Content.Span, preview.IsTruncated);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preview), preview.Kind, null);
        }
    }

    private void ApplyFilter()
    {
        var query = Filter.Trim();
        var selected = SelectedEntry;
        var visible = _allEntries
            .Where(item => query.Length == 0
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item, Comparer<FilePanelEntry>.Create(CompareEntries))
            .Select(item => selected is not null && ReferenceEquals(selected.Entry, item)
                ? selected
                : new FileEntryViewModel(item))
            .ToArray();
        Replace(Entries, visible);
        if (selected is not null && !visible.Contains(selected))
        {
            SelectedEntry = null;
        }

        OnPropertyChanged(nameof(ShowEmptyState));
        OnContentPresentationChanged();
    }

    private void UpdateListingStatus()
    {
        if (!_hasLoadedListing || IsLoading || ContentIssue is not null)
        {
            return;
        }

        if (_allEntries.Count == 0)
        {
            Status = "This location is empty";
            return;
        }

        var loadedCount = _allEntries.Count.ToString(CultureInfo.InvariantCulture);
        var loadedLabel = HasMore
            ? $"{loadedCount} loaded item(s)"
            : $"{loadedCount} item(s)";
        Status = string.IsNullOrWhiteSpace(Filter)
            ? loadedLabel
            : $"{Entries.Count.ToString(CultureInfo.InvariantCulture)} of {loadedLabel}";
    }

    private int CompareEntries(FilePanelEntry? left, FilePanelEntry? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var directoryOrder = CompareDirectoryGroup(left, right);
        if (directoryOrder != 0)
        {
            return directoryOrder;
        }

        var missingValueOrder = SortField switch
        {
            FileEntrySortField.Size => CompareMissing(left.Size is null, right.Size is null),
            FileEntrySortField.Modified => CompareMissing(
                left.LastModifiedAt is null,
                right.LastModifiedAt is null),
            _ => 0,
        };
        if (missingValueOrder != 0)
        {
            return missingValueOrder;
        }

        var primaryOrder = SortField switch
        {
            FileEntrySortField.Name => StringComparer.OrdinalIgnoreCase.Compare(
                left.Name,
                right.Name),
            FileEntrySortField.Kind => left.Kind.CompareTo(right.Kind),
            FileEntrySortField.Size => CompareNullable(left.Size, right.Size),
            FileEntrySortField.Modified => CompareNullable(
                left.LastModifiedAt,
                right.LastModifiedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(SortField), SortField, null),
        };
        if (primaryOrder != 0)
        {
            return SortDirection == FileEntrySortDirection.Ascending
                ? primaryOrder
                : -primaryOrder;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    }

    private static int CompareDirectoryGroup(FilePanelEntry left, FilePanelEntry right) =>
        (left.Kind == FilePanelEntryKind.Directory ? 0 : 1)
        .CompareTo(right.Kind == FilePanelEntryKind.Directory ? 0 : 1);

    private static int CompareMissing(bool leftMissing, bool rightMissing) =>
        (leftMissing ? 1 : 0).CompareTo(rightMissing ? 1 : 0);

    private static int CompareNullable<T>(T? left, T? right)
        where T : struct, IComparable<T>
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        return right is null ? -1 : left.Value.CompareTo(right.Value);
    }

    private void ClearPreview()
    {
        PreviewImage = null;
        PreviewText = null;
        PreviewIssue = null;
        PreviewTitle = "Preview";
        ClearDatabasePreview();
    }

    /// <summary>
    /// Disposes the previewed database and the temporary copy behind it. The
    /// copy is deleted as soon as the selection moves on, so browsing a folder
    /// of databases never accumulates them.
    /// </summary>
    /// <summary>
    /// The ceiling on a database opened through the file preview. A remote
    /// database is copied in full before it can be opened — a partial copy is a
    /// corrupt database — so the limit is what we are willing to pull, not what
    /// we are willing to render.
    /// </summary>
    private const long MaximumDatabasePreviewBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Opens the selected file with the database viewer. Local files open where
    /// they are; a file on any other provider is copied to a private temporary
    /// file first, because a database engine opens a path, not a byte stream.
    /// </summary>
    private async Task OpenDatabasePreviewAsync(FilePanelLocation location)
    {
        if (_databaseClient is null || _materializer is null)
        {
            PreviewText = "This build cannot open database files in the preview.";
            return;
        }

        var operation = _preview;
        if (operation is null)
        {
            return;
        }

        IsPreviewLoading = true;
        FilePanelResult<MaterializedFile> result;
        try
        {
            result = await _materializer.MaterializeAsync(
                location,
                MaximumDatabasePreviewBytes,
                operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = "The database could not be opened.";
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
            }
        }

        // The selection moved while the copy was in flight: the lease belongs
        // to a preview nobody is looking at any more.
        if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
        {
            result.Value?.Dispose();
            return;
        }

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            PreviewText = error.Message;
            PreviewIssue = FileOperationIssue.FromProvider(error);
            return;
        }

        var file = result.Value!;
        var viewer = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            PreviewTitle,
            _databaseClient,
            SqliteDriverId,
            file.Path);
        _databasePreviewFile = file;
        _databasePreview = viewer;
        OnPropertyChanged(nameof(DatabasePreview));
        OnPropertyChanged(nameof(HasDatabasePreview));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ShowPreviewPlaceholder));
        await viewer.ConnectAsync();
    }

    private const string SqliteDriverId = "sqlite";

    private void ClearDatabasePreview()
    {
        var preview = _databasePreview;
        var file = _databasePreviewFile;
        _databasePreview = null;
        _databasePreviewFile = null;
        if (preview is not null)
        {
            OnPropertyChanged(nameof(DatabasePreview));
            OnPropertyChanged(nameof(HasDatabasePreview));
        }

        preview?.Dispose();
        file?.Dispose();
    }

    private void ClearMetadata()
    {
        _metadata?.Cancel();
        SelectedMetadata = null;
        MetadataIssue = null;
        IsMetadataLoading = false;
    }

    private void SetContentIssue(FileOperationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        OperationIssue = null;
        ContentIssue = issue;
    }

    private void SetOperationIssue(FileOperationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        OperationIssue = issue;
    }

    private void ResetListing()
    {
        _allEntries.Clear();
        _continuationToken = null;
        _hasLoadedListing = false;
        OnPropertyChanged(nameof(HasListingSummary));
        SelectedEntry = null;
        ClearMetadata();
        _preview?.Cancel();
        IsPreviewLoading = false;
        ClearPreview();
        Replace(Entries, []);
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnContentPresentationChanged();
    }

    private void PublishIssueState()
    {
        OnPropertyChanged(nameof(CurrentIssue));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorTitle));
        OnPropertyChanged(nameof(ErrorSuggestedAction));
        OnPropertyChanged(nameof(CanRetryError));
    }

    private void OnContentPresentationChanged()
    {
        OnPropertyChanged(nameof(ContentPresentation));
        OnPropertyChanged(nameof(ContentState));
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(ShowNavigationProgress));
        OnPropertyChanged(nameof(ShowEmptyLocationState));
        OnPropertyChanged(nameof(ShowSearchNoResultsState));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(CanRetryContentState));
        OnPropertyChanged(nameof(ShowEmptyState));
        _retryCommand.RaiseCanExecuteChanged();
    }

    private static string ChildLocationDisplay(FilePanelLocation parent, string name)
    {
        var displayed = FileLocationPresentation.Display(parent);
        return parent.Address switch
        {
            FilePanelAddress.Hierarchical => displayed == "/"
                ? $"/{name}"
                : $"{displayed.TrimEnd('/')}/{name}",
            FilePanelAddress.ObjectKey or FilePanelAddress.ContainerRoot =>
                displayed.Length == 0 ? name : $"{displayed.TrimEnd('/')}/{name}",
            _ => name,
        };
    }

    private static string BuiltInHomePath()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)
            ? Path.GetFullPath(AppContext.BaseDirectory)
            : Path.GetFullPath(path);
    }

    private static bool IsWithinDirectory(string directory, string candidate)
    {
        var relativePath = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private CancellationTokenSource ReplaceNavigation(CancellationToken cancellationToken)
    {
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        return _navigation;
    }

    private static string FormatJson(ReadOnlySpan<byte> content, bool truncated)
    {
        if (truncated)
        {
            return Encoding.UTF8.GetString(content) + "\n\n[preview truncated]";
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch (JsonException)
        {
            return Encoding.UTF8.GetString(content);
        }
    }

    private static string FormatHex(ReadOnlySpan<byte> content, bool providerTruncated)
    {
        var shown = content[..Math.Min(content.Length, MaximumFormattedBinaryBytes)];
        var builder = new StringBuilder((shown.Length / 16 + 1) * 72);
        for (var offset = 0; offset < shown.Length; offset += 16)
        {
            var row = shown.Slice(offset, Math.Min(16, shown.Length - offset));
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
            builder.Append("  ");
            for (var index = 0; index < 16; index++)
            {
                if (index < row.Length)
                {
                    builder.Append(row[index].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("  ");
                }

                builder.Append(index == 7 ? "  " : " ");
            }

            builder.Append(" | ");
            foreach (var value in row)
            {
                builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }

            builder.AppendLine();
        }

        if (providerTruncated || shown.Length < content.Length)
        {
            builder.AppendLine("[preview truncated]");
        }

        return builder.ToString();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
