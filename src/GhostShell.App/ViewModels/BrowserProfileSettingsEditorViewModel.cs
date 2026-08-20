using System.Windows.Input;
using GhostShell.Application;

namespace GhostShell.App.ViewModels;

public sealed record BrowserProfileSharingOption(
    BrowserProfileSharing Sharing,
    string DisplayName,
    string Description);

/// <summary>
/// Edits the browser profile default and exposes deliberate clearing for each
/// in-memory profile family and any legacy stored data. Open tabs retain their
/// launch profile.
/// </summary>
public sealed class BrowserProfileSettingsEditorViewModel : ObservableObject
{
    private readonly IBrowserProfilePreferences _preferences;
    private readonly IBrowserProfileDataControl? _dataControl;
    private string _usageText =
        "Open Browser settings to measure legacy stored site data.";
    private string? _operationStatus;
    private int _usageRefreshGeneration;

    public BrowserProfileSettingsEditorViewModel(
        IBrowserProfilePreferences preferences,
        IBrowserProfileDataControl? dataControl = null)
    {
        _preferences = preferences
            ?? throw new ArgumentNullException(nameof(preferences));
        _dataControl = dataControl;
        SharingOptions =
        [
            new(
                BrowserProfileSharing.Shared,
                "Shared across workspaces",
                "Browser tabs share one cookie jar and site-storage profile for this app session."),
            new(
                BrowserProfileSharing.PerWorkspace,
                "Separate by workspace",
                "Each workspace uses separate cookies and site storage for this app session unless it overrides this setting."),
        ];
        ClearGlobalCommand = new AsyncActionCommand(
            () => ClearAsync(BrowserProfileDataScope.Global),
            () => _dataControl is not null);
        ClearWorkspacesCommand = new AsyncActionCommand(
            () => ClearAsync(BrowserProfileDataScope.Workspaces),
            () => _dataControl is not null);
        ClearAllCommand = new AsyncActionCommand(
            () => ClearAsync(BrowserProfileDataScope.All),
            () => _dataControl is not null);
    }

    public IReadOnlyList<BrowserProfileSharingOption> SharingOptions { get; }

    public BrowserProfileSharingOption SelectedSharing
    {
        get => SharingOptions.Single(option =>
            option.Sharing == _preferences.Current.Sharing);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Sharing == _preferences.Current.Sharing)
            {
                return;
            }

            _ = _preferences.ApplyAsync(
                new BrowserProfileSettings(value.Sharing),
                CancellationToken.None).AsTask();
            OnPropertyChanged();
        }
    }

    public ICommand ClearGlobalCommand { get; }

    public ICommand ClearWorkspacesCommand { get; }

    public ICommand ClearAllCommand { get; }

    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    public string? OperationStatus
    {
        get => _operationStatus;
        private set
        {
            if (SetProperty(ref _operationStatus, value))
            {
                OnPropertyChanged(nameof(HasOperationStatus));
            }
        }
    }

    public bool HasOperationStatus => OperationStatus is not null;

    public void RefreshUsage()
    {
        if (_dataControl is null)
        {
            UsageText = "Browser profile storage is unavailable in this build.";
            return;
        }

        var generation = Interlocked.Increment(ref _usageRefreshGeneration);
        UsageText = "Measuring stored browser data…";
        _ = RefreshUsageAsync(generation);
    }

    private async Task ClearAsync(BrowserProfileDataScope scope)
    {
        if (_dataControl is null)
        {
            return;
        }

        var result = await _dataControl.ClearAsync(scope, CancellationToken.None);
        OperationStatus = result.Message;
        RefreshUsage();
    }

    private async Task RefreshUsageAsync(int generation)
    {
        BrowserProfileStorageUsage usage;
        try
        {
            usage = await Task.Run(
                () => _dataControl!.ReadUsage()).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ObjectDisposedException)
        {
            if (generation == Volatile.Read(ref _usageRefreshGeneration))
            {
                UsageText = "Stored browser data could not be measured.";
            }

            return;
        }

        if (generation != Volatile.Read(ref _usageRefreshGeneration))
        {
            return;
        }

        UsageText = $"Shared {ByteSize.Format(usage.GlobalBytes)} · "
            + $"workspaces {ByteSize.Format(usage.WorkspaceBytes)} · "
            + $"WebApps {ByteSize.Format(usage.WebAppBytes)}";
    }
}
