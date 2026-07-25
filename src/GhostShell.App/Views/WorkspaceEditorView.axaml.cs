using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Views;

/// <summary>
/// Edits one isolated workspace snapshot and reports host-owned save and close intents.
/// The control applies reversible in-editor operations itself; persistence and discard
/// confirmation remain responsibilities of the containing window.
/// </summary>
public sealed partial class WorkspaceEditorView : UserControl
{
    private WorkspaceEditorViewModel? _observedEditor;
    private bool _syncingIcon;

    public WorkspaceEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveEditor();
    }

    public WorkspaceEditorView(WorkspaceEditorViewModel editor)
        : this()
    {
        DataContext = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    /// <summary>Raised with a validated, immutable save request. The host owns persistence.</summary>
    public event EventHandler<WorkspaceEditorSaveRequestedEventArgs>? SaveRequested;

    /// <summary>Raised with the view model's close or confirm-discard disposition.</summary>
    public event EventHandler<WorkspaceEditorCancelRequestedEventArgs>? CancelRequested;

    /// <summary>Raised after the original workspace snapshot has been restored.</summary>
    public event EventHandler? ResetRequested;

    public WorkspaceEditorViewModel? Editor => DataContext as WorkspaceEditorViewModel;

    public bool FocusInitialControl() =>
        this.FindControl<TextBox>("WorkspaceNameInput")!.Focus();

    private ListBox EntryListControl => this.FindControl<ListBox>("EntryList")!;

    private ScrollViewer SelectedEntryEditorControl =>
        this.FindControl<ScrollViewer>("SelectedEntryEditor")!;

    private Border NoEntrySelectionControl => this.FindControl<Border>("NoEntrySelection")!;

    private void ObserveEditor()
    {
        if (_observedEditor is not null)
        {
            _observedEditor.PropertyChanged -= OnEditorPropertyChanged;
        }

        _observedEditor = Editor;
        if (_observedEditor is not null)
        {
            _observedEditor.PropertyChanged += OnEditorPropertyChanged;
        }

        ConfigurePickers();
        EnsureEntrySelection();
    }

    private void ConfigurePickers()
    {
        var editor = Editor;
        var iconPicker = this.FindControl<ComboBox>("IconPicker")!;
        var addConnectionPicker = this.FindControl<ComboBox>("AddConnectionPicker")!;
        var addScreenPicker = this.FindControl<ComboBox>("AddScreenPicker")!;
        var addLayoutPicker = this.FindControl<ComboBox>("AddLayoutPicker")!;
        var entryConnectionPicker = this.FindControl<ComboBox>("EntryConnectionPicker")!;
        var entryScreenPicker = this.FindControl<ComboBox>("EntryScreenPicker")!;

        if (editor is null)
        {
            iconPicker.ItemsSource = null;
            addConnectionPicker.ItemsSource = null;
            addScreenPicker.ItemsSource = null;
            addLayoutPicker.ItemsSource = null;
            entryConnectionPicker.ItemsSource = null;
            entryScreenPicker.ItemsSource = null;
            return;
        }

        iconPicker.ItemsSource = editor.IconOptions;
        addConnectionPicker.ItemsSource = editor.ConnectionOptions;
        addScreenPicker.ItemsSource = editor.ScreenOptions;
        addLayoutPicker.ItemsSource = editor.LayoutOptions;
        entryConnectionPicker.ItemsSource = editor.ConnectionOptions;
        entryScreenPicker.ItemsSource = editor.ScreenOptions;
        addConnectionPicker.SelectedItem = editor.ConnectionOptions.FirstOrDefault(option => option.IsAvailable);
        addScreenPicker.SelectedItem = editor.ScreenOptions.FirstOrDefault(option => option.IsAvailable);
        addLayoutPicker.SelectedItem = editor.LayoutOptions.FirstOrDefault(option => option.IsAvailable);
        SynchronizeIconPicker();
    }

    private void SynchronizeIconPicker()
    {
        var editor = Editor;
        if (editor is null)
        {
            return;
        }

        _syncingIcon = true;
        this.FindControl<ComboBox>("IconPicker")!.SelectedItem =
            editor.IconOptions.SingleOrDefault(option => option.Id == editor.Icon);
        _syncingIcon = false;
    }

    private void EnsureEntrySelection()
    {
        var editor = Editor;
        if (editor is null || editor.Entries.Count == 0)
        {
            EntryListControl.SelectedItem = null;
            ShowSelectedEntry(null);
            return;
        }

        if (EntryListControl.SelectedItem is not WorkspaceEntryEditorViewModel selected
            || !editor.Entries.Contains(selected))
        {
            EntryListControl.SelectedItem = editor.Entries[0];
        }

        ShowSelectedEntry(EntryListControl.SelectedItem as WorkspaceEntryEditorViewModel);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(WorkspaceEditorViewModel.Icon))
        {
            SynchronizeIconPicker();
        }
        else if (e.PropertyName == nameof(WorkspaceEditorViewModel.Entries))
        {
            EnsureEntrySelection();
        }
    }

    private void OnIconSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (_syncingIcon
            || sender is not ComboBox { SelectedItem: WorkspaceIconOption option }
            || Editor is not { } editor)
        {
            return;
        }

        editor.Icon = option.Id;
    }

    private void OnEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        ShowSelectedEntry((sender as ListBox)?.SelectedItem as WorkspaceEntryEditorViewModel);
    }

    private void ShowSelectedEntry(WorkspaceEntryEditorViewModel? entry)
    {
        SelectedEntryEditorControl.DataContext = entry;
        SelectedEntryEditorControl.IsVisible = entry is not null;
        NoEntrySelectionControl.IsVisible = entry is null;
    }

    private void OnAddConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor
            || this.FindControl<ComboBox>("AddConnectionPicker")!.SelectedItem
                is not ScreenConnectionOption option)
        {
            ShowInteractionError("Choose an available connection first.");
            return;
        }

        CompleteAdd(editor.AddConnection(option.Id));
    }

    private void OnAddSavedScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor
            || this.FindControl<ComboBox>("AddScreenPicker")!.SelectedItem
                is not WorkspaceScreenOption option)
        {
            ShowInteractionError("Choose an available saved screen first.");
            return;
        }

        CompleteAdd(editor.AddSavedScreen(option.Id));
    }

    private void OnCopySavedScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor
            || this.FindControl<ComboBox>("AddScreenPicker")!.SelectedItem
                is not WorkspaceScreenOption option)
        {
            ShowInteractionError("Choose an available saved screen first.");
            return;
        }

        CompleteAdd(editor.AddWorkspaceTabFromScreen(option.Id));
    }

    private void OnAddWorkspaceTabClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor
            || this.FindControl<ComboBox>("AddLayoutPicker")!.SelectedItem
                is not WorkspaceLayoutOption option)
        {
            ShowInteractionError("Choose an available layout first.");
            return;
        }

        CompleteAdd(editor.AddWorkspaceTab(option.Id));
    }

    private void CompleteAdd(WorkspaceEditorOperationResult result)
    {
        if (!result.IsSuccess || result.EntryId is not { } entryId || Editor is not { } editor)
        {
            ShowInteractionError(result.Error ?? "The workspace item could not be added.");
            return;
        }

        ClearInteractionError();
        EntryListControl.SelectedItem = editor.Entries.Single(entry => entry.Id == entryId);
        EntryListControl.ScrollIntoView(EntryListControl.SelectedItem!);
    }

    private void OnMoveEntryEarlierClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: WorkspaceEntryEditorViewModel entry }
            && Editor is { } editor)
        {
            CompleteEntryOperation(editor.MoveEntryEarlier(entry.Id), entry);
        }
    }

    private void OnMoveEntryLaterClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: WorkspaceEntryEditorViewModel entry }
            && Editor is { } editor)
        {
            CompleteEntryOperation(editor.MoveEntryLater(entry.Id), entry);
        }
    }

    private void CompleteEntryOperation(
        WorkspaceEditorOperationResult result,
        WorkspaceEntryEditorViewModel entry)
    {
        if (!result.IsSuccess)
        {
            ShowInteractionError(result.Error ?? "The workspace order could not be changed.");
            return;
        }

        ClearInteractionError();
        EntryListControl.SelectedItem = entry;
        EntryListControl.ScrollIntoView(entry);
    }

    private void OnRemoveEntryClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceEntryEditorViewModel entry }
            || Editor is not { } editor)
        {
            return;
        }

        var index = editor.Entries.IndexOf(entry);
        var result = editor.RemoveEntry(entry.Id);
        if (!result.IsSuccess)
        {
            ShowInteractionError(result.Error ?? "The workspace item could not be removed.");
            return;
        }

        ClearInteractionError();
        EntryListControl.SelectedIndex = Math.Min(index, editor.Entries.Count - 1);
        EnsureEntrySelection();
    }

    private void OnAddTerminalPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Terminal);

    private void OnAddFilePanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.FileViewer);

    private void OnAddBrowserPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Browser);

    private void OnAddStatisticsPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.Statistics);

    private void OnAddProcessMonitorPanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.ProcessMonitor);

    private void AddPanel(object? sender, RoutedEventArgs e, ScreenPanelKind kind)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceTabEditorViewModel tab })
        {
            return;
        }

        if (!tab.AddPanel(kind))
        {
            ShowInteractionError("The selected layout has no unused slot for another panel.");
            return;
        }

        ClearInteractionError();
    }

    private void OnMovePanelEarlierClick(object? sender, RoutedEventArgs e) =>
        MovePanel(sender, e, -1);

    private void OnMovePanelLaterClick(object? sender, RoutedEventArgs e) =>
        MovePanel(sender, e, 1);

    private void MovePanel(object? sender, RoutedEventArgs e, int offset)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceTabPanelEditorViewModel panel }
            || EntryListControl.SelectedItem is not WorkspaceEntryEditorViewModel { Tab: { } tab })
        {
            return;
        }

        var destination = tab.Panels.IndexOf(panel) + offset;
        if (!tab.MovePanel(panel.Id, destination))
        {
            ShowInteractionError(offset < 0
                ? "The panel is already first."
                : "The panel is already last.");
            return;
        }

        ClearInteractionError();
    }

    private void OnRemovePanelClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: WorkspaceTabPanelEditorViewModel panel }
            || EntryListControl.SelectedItem is not WorkspaceEntryEditorViewModel { Tab: { } tab })
        {
            return;
        }

        if (!tab.RemovePanel(panel.Id))
        {
            ShowInteractionError("The panel is no longer part of this tab.");
            return;
        }

        ClearInteractionError();
    }

    private void OnClearOperationErrorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Editor?.ClearOperationError();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor)
        {
            return;
        }

        editor.Reset();
        ClearInteractionError();
        SynchronizeIconPicker();
        EnsureEntrySelection();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is { } editor)
        {
            CancelRequested?.Invoke(
                this,
                new WorkspaceEditorCancelRequestedEventArgs(editor.RequestCancel()));
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is not { } editor)
        {
            return;
        }

        try
        {
            var request = editor.CreateSaveRequest();
            ClearInteractionError();
            SaveRequested?.Invoke(this, new WorkspaceEditorSaveRequestedEventArgs(request));
        }
        catch (InvalidOperationException exception)
        {
            ShowInteractionError(exception.Message);
        }
    }

    private void ShowInteractionError(string message)
    {
        this.FindControl<TextBlock>("InteractionErrorText")!.Text = message;
        this.FindControl<Border>("InteractionErrorCard")!.IsVisible = true;
    }

    private void ClearInteractionError()
    {
        this.FindControl<TextBlock>("InteractionErrorText")!.Text = string.Empty;
        this.FindControl<Border>("InteractionErrorCard")!.IsVisible = false;
    }
}
