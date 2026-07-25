using System.ComponentModel;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Presents the three durable workspace entry variants through one ordered list while
/// retaining the variant-specific editor needed to rebuild the original union member.
/// </summary>
public sealed class WorkspaceEntryEditorViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceEntry _original;
    private string _alias;
    private ScreenConnectionOption? _selectedConnection;
    private WorkspaceScreenOption? _selectedScreen;
    private bool _disposed;

    private WorkspaceEntryEditorViewModel(
        WorkspaceEntry original,
        WorkspaceEditorEntryKind kind,
        ScreenConnectionOption? selectedConnection,
        WorkspaceScreenOption? selectedScreen,
        WorkspaceTabEditorViewModel? tab)
    {
        _original = original ?? throw new ArgumentNullException(nameof(original));
        Kind = kind;
        _selectedConnection = selectedConnection;
        _selectedScreen = selectedScreen;
        Tab = tab;
        _alias = original switch
        {
            WorkspaceEntry.ConnectionReference connection => connection.Alias ?? string.Empty,
            WorkspaceEntry.ScreenReference screen => screen.Alias ?? string.Empty,
            _ => string.Empty,
        };
        if (Tab is not null)
        {
            Tab.PropertyChanged += OnTabChanged;
        }
    }

    public WorkspaceEntryId Id => _original.Id;

    public WorkspaceEditorEntryKind Kind { get; }

    public bool IsConnection => Kind == WorkspaceEditorEntryKind.Connection;

    public bool IsSavedScreen => Kind == WorkspaceEditorEntryKind.SavedScreen;

    public bool IsWorkspaceTab => Kind == WorkspaceEditorEntryKind.WorkspaceTab;

    public bool CanEditAlias => !IsWorkspaceTab;

    public string KindLabel => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => "CONNECTION",
        WorkspaceEditorEntryKind.SavedScreen => "SAVED SCREEN",
        WorkspaceEditorEntryKind.WorkspaceTab => "WORKSPACE TAB",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value))
            {
                PublishDisplayState();
            }
        }
    }

    public ScreenConnectionOption? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (!IsConnection)
            {
                throw new InvalidOperationException("Only connection entries select a connection.");
            }

            if (SetProperty(ref _selectedConnection, value))
            {
                PublishDisplayState();
            }
        }
    }

    public WorkspaceScreenOption? SelectedScreen
    {
        get => _selectedScreen;
        set
        {
            if (!IsSavedScreen)
            {
                throw new InvalidOperationException("Only saved-screen entries select a screen.");
            }

            if (SetProperty(ref _selectedScreen, value))
            {
                PublishDisplayState();
            }
        }
    }

    public WorkspaceTabEditorViewModel? Tab { get; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Alias))
            {
                return Alias.Trim();
            }

            return Kind switch
            {
                WorkspaceEditorEntryKind.Connection => SelectedConnection?.Name ?? "Missing connection",
                WorkspaceEditorEntryKind.SavedScreen => SelectedScreen?.Name ?? "Missing saved screen",
                WorkspaceEditorEntryKind.WorkspaceTab => Tab?.Name ?? "Workspace tab",
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
    }

    public string Detail => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => SelectedConnection?.DisplayName ?? "Select a connection",
        WorkspaceEditorEntryKind.SavedScreen => SelectedScreen?.DisplayName ?? "Select a saved screen",
        WorkspaceEditorEntryKind.WorkspaceTab => Tab is null
            ? "Tab definition unavailable"
            : $"{Tab.SelectedLayout.DisplayName} · {Tab.Panels.Count} panel{(Tab.Panels.Count == 1 ? string.Empty : "s")}",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public bool HasMissingReference => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => SelectedConnection?.IsAvailable != true,
        WorkspaceEditorEntryKind.SavedScreen => SelectedScreen?.IsAvailable != true,
        WorkspaceEditorEntryKind.WorkspaceTab => Tab?.HasMissingDefinition != false,
        _ => true,
    };

    public string ReferenceStatus => HasMissingReference ? "REPAIR REQUIRED" : "AVAILABLE";

    internal static WorkspaceEntryEditorViewModel Create(
        WorkspaceEntry entry,
        IReadOnlyList<ScreenConnectionOption> connectionOptions,
        IReadOnlyList<WorkspaceScreenOption> screenOptions,
        IReadOnlyList<WorkspaceLayoutOption> layoutOptions,
        IReadOnlyList<ScreenFileProviderOption> fileProviderOptions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry switch
        {
            WorkspaceEntry.ConnectionReference connection => new(
                connection,
                WorkspaceEditorEntryKind.Connection,
                connectionOptions.Single(option => option.Id == connection.ConnectionId),
                null,
                null),
            WorkspaceEntry.ScreenReference screen => new(
                screen,
                WorkspaceEditorEntryKind.SavedScreen,
                null,
                screenOptions.Single(option => option.Id == screen.ScreenId),
                null),
            WorkspaceEntry.Tab tab => new(
                tab,
                WorkspaceEditorEntryKind.WorkspaceTab,
                null,
                null,
                new WorkspaceTabEditorViewModel(
                    tab,
                    layoutOptions,
                    connectionOptions,
                    fileProviderOptions)),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown workspace entry type."),
        };
    }

    internal WorkspaceEntry Build() => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => new WorkspaceEntry.ConnectionReference(
            Id,
            SelectedConnection?.Id
                ?? ((WorkspaceEntry.ConnectionReference)_original).ConnectionId,
            Alias),
        WorkspaceEditorEntryKind.SavedScreen => new WorkspaceEntry.ScreenReference(
            Id,
            SelectedScreen?.Id
                ?? ((WorkspaceEntry.ScreenReference)_original).ScreenId,
            Alias),
        WorkspaceEditorEntryKind.WorkspaceTab => Tab?.Build()
            ?? throw new InvalidOperationException("A workspace tab requires its tab definition."),
        _ => throw new ArgumentOutOfRangeException(),
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Tab is not null)
        {
            Tab.PropertyChanged -= OnTabChanged;
            Tab.Dispose();
        }

        _disposed = true;
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        PublishDisplayState();
    }

    private void PublishDisplayState()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasMissingReference));
        OnPropertyChanged(nameof(ReferenceStatus));
    }
}
