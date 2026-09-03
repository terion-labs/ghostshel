using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class WorkspaceIsolationMountEditorViewModel : ObservableObject
{
    private string _hostPath;
    private string _guestPath;
    private bool _isReadOnly;

    internal WorkspaceIsolationMountEditorViewModel(
        string hostPath,
        string guestPath,
        bool isReadOnly)
    {
        _hostPath = hostPath;
        _guestPath = guestPath;
        _isReadOnly = isReadOnly;
    }

    public string HostPath
    {
        get => _hostPath;
        set => SetProperty(ref _hostPath, value ?? string.Empty);
    }

    public string GuestPath
    {
        get => _guestPath;
        set
        {
            if (SetProperty(ref _guestPath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(RemoveAccessibleName));
            }
        }
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => SetProperty(ref _isReadOnly, value);
    }

    public string RemoveAccessibleName => string.IsNullOrWhiteSpace(GuestPath)
        ? "Remove host mount"
        : $"Remove host mount at {GuestPath}";

    internal WorkspaceIsolationMountDefinition Build() => new(
        HostPath.Trim(),
        GuestPath.Trim(),
        IsReadOnly);
}
