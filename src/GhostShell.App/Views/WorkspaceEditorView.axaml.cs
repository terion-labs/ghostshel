using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using GhostShell.App.ViewModels;
using GhostShell.Core;

using GhostShell.App.Controls;

namespace GhostShell.App.Views;

/// <summary>
/// Edits one isolated workspace snapshot and reports host-owned save and close intents.
/// The control applies reversible in-editor operations itself; persistence and discard
/// confirmation remain responsibilities of the containing window.
/// </summary>
public sealed partial class WorkspaceEditorView : UserControl
{
    private WorkspaceEditorViewModel? _observedEditor;
    private bool _syncingAccent;

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

    /// <summary>
    /// Asks the host to sample a colour from the screen. Screen sampling is a
    /// platform capability, so the editor requests it rather than owning it.
    /// </summary>
    public event EventHandler? PickAccentRequested;

    public WorkspaceEditorViewModel? Editor => DataContext as WorkspaceEditorViewModel;

    public bool FocusInitialControl() =>
        this.FindControl<TextBox>("WorkspaceNameInput")!.Focus();

    private ListBox EntryListControl => this.FindControl<ListBox>("EntryList")!;

    private ScrollViewer SelectedEntryEditorControl =>
        this.FindControl<ScrollViewer>("SelectedEntryEditor")!;

    private SurfaceCard NoEntrySelectionControl => this.FindControl<SurfaceCard>("NoEntrySelection")!;

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
        // After the binding pass, not during DataContextChanged: the entry
        // list's ItemsSource has not delivered the new editor's entries yet, so
        // selecting the first entry now is coerced back to nothing — which is
        // why reopening the editor used to land on the empty state.
        Avalonia.Threading.Dispatcher.UIThread.Post(EnsureEntrySelection);
    }

    private void ConfigurePickers()
    {
        var editor = Editor;
        var addConnectionPicker = this.FindControl<ComboBox>("AddConnectionPicker")!;
        var addScreenPicker = this.FindControl<ComboBox>("AddScreenPicker")!;
        var addLayoutPicker = this.FindControl<ComboBox>("AddLayoutPicker")!;
        var entryConnectionPicker = this.FindControl<ComboBox>("EntryConnectionPicker")!;
        var entryScreenPicker = this.FindControl<ComboBox>("EntryScreenPicker")!;

        if (editor is null)
        {
            addConnectionPicker.ItemsSource = null;
            addScreenPicker.ItemsSource = null;
            addLayoutPicker.ItemsSource = null;
            entryConnectionPicker.ItemsSource = null;
            entryScreenPicker.ItemsSource = null;
            return;
        }

        // Auto-saved layouts carry a live tab's captured geometry; they resolve
        // existing tab references but are not offered for new tabs.
        var pickableLayouts = editor.LayoutOptions
            .Where(option => !LayoutDefinition.IsAutoSaved(option.Id))
            .ToArray();
        addConnectionPicker.ItemsSource = editor.ConnectionOptions;
        addScreenPicker.ItemsSource = editor.ScreenOptions;
        addLayoutPicker.ItemsSource = pickableLayouts;
        entryConnectionPicker.ItemsSource = editor.ConnectionOptions;
        entryScreenPicker.ItemsSource = editor.ScreenOptions;
        addConnectionPicker.SelectedItem = editor.ConnectionOptions.FirstOrDefault(option => option.IsAvailable);
        addScreenPicker.SelectedItem = editor.ScreenOptions.FirstOrDefault(option => option.IsAvailable);
        addLayoutPicker.SelectedItem = pickableLayouts.FirstOrDefault(option => option.IsAvailable);
        SynchronizeIconPicker();
    }

    /// <summary>
    /// Selection in the icon and accent grids is bound declaratively, so only the
    /// colour picker — which holds its own value — needs pushing.
    /// </summary>
    private void SynchronizeIconPicker() => SynchronizeAccentPicker();

    private void SynchronizeAccentPicker()
    {
        var editor = Editor;
        if (editor is null || _syncingAccent)
        {
            return;
        }

        // A workspace with no accent yet still needs a sensible starting colour in
        // the picker; a half-typed hex is not a colour, so the picker keeps its
        // previous value rather than jumping to black on every keystroke.
        if (string.IsNullOrWhiteSpace(editor.Accent))
        {
            SetPickerColor(Color.Parse(ThemePreference.BronzeFallback.ToString()));
            return;
        }

        if (!Color.TryParse(editor.Accent, out var color))
        {
            return;
        }

        SetPickerColor(color);
    }

    private void SetPickerColor(Color color)
    {
        _syncingAccent = true;
        try
        {
            this.FindControl<ColorPicker>("AccentColorPicker")!.Color = color;
        }
        finally
        {
            _syncingAccent = false;
        }
    }

    private void OnIconChoiceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is { } editor
            && sender is Button { DataContext: WorkspaceIconOption option })
        {
            editor.Icon = option.Id;
        }
    }

    private void OnAccentPresetClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is { } editor
            && sender is Button { DataContext: WorkspaceAccentOption option })
        {
            editor.Accent = option.Hex;
        }
    }

    private void OnAccentColorChanged(object? sender, ColorChangedEventArgs e)
    {
        _ = sender;
        if (Editor is not { } editor || _syncingAccent)
        {
            return;
        }

        _syncingAccent = true;
        try
        {
            editor.Accent = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
        }
        finally
        {
            _syncingAccent = false;
        }
    }

    private void OnPickAccentClick(object? sender, RoutedEventArgs e) =>
        PickAccentRequested?.Invoke(sender, e);

    /// <summary>
    /// Applies a colour sampled by the host. Kept separate from
    /// <see cref="OnAccentColorChanged"/> so a sample is a deliberate edit rather
    /// than a picker echo.
    /// </summary>
    public void ApplySampledAccent(Color color)
    {
        if (Editor is { } editor)
        {
            editor.Accent = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
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
        if (e.PropertyName is nameof(WorkspaceEditorViewModel.Accent))
        {
            SynchronizeIconPicker();
        }
        else if (e.PropertyName == nameof(WorkspaceEditorViewModel.Entries))
        {
            EnsureEntrySelection();
        }
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

        var nameInput = this.FindControl<TextBox>("AddTabNameInput")!;
        var name = string.IsNullOrWhiteSpace(nameInput.Text) ? "New tab" : nameInput.Text.Trim();
        var result = editor.AddWorkspaceTab(option.Id, name);
        if (result.IsSuccess)
        {
            nameInput.Text = string.Empty;
        }

        CompleteAdd(result);
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

    private void OnAddDatabasePanelClick(object? sender, RoutedEventArgs e) =>
        AddPanel(sender, e, ScreenPanelKind.DatabaseViewer);

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
        this.FindControl<SurfaceCard>("InteractionErrorCard")!.IsVisible = true;
    }

    private void ClearInteractionError()
    {
        this.FindControl<TextBlock>("InteractionErrorText")!.Text = string.Empty;
        this.FindControl<SurfaceCard>("InteractionErrorCard")!.IsVisible = false;
    }
}
