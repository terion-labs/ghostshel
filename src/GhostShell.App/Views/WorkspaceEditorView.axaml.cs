using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Views;

/// <summary>
/// Edits one isolated workspace snapshot and reports host-owned save and close intents.
/// The control applies reversible in-editor operations itself; persistence, discard
/// confirmation, and switching to another workspace remain the containing window's.
/// </summary>
public sealed partial class WorkspaceEditorView : UserControl
{
    private WorkspaceEditorViewModel? _observedEditor;
    private bool _syncingAccent;
    private bool _syncingColor;
    private bool _syncingPeers;
    private WorkspaceEntryEditorViewModel? _draggingEntry;

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

    /// <summary>
    /// Raised when the rail asks for a different workspace. The editor holds one
    /// snapshot and cannot swap it for another, so the host decides what happens
    /// to the edits in progress before opening the next one.
    /// </summary>
    public event EventHandler<WorkspaceId>? WorkspaceSelectionRequested;

    /// <summary>Raised when the rail's plus asks for a workspace that does not exist yet.</summary>
    public event EventHandler? CreateWorkspaceRequested;

    public WorkspaceEditorViewModel? Editor => DataContext as WorkspaceEditorViewModel;

    public bool FocusInitialControl() =>
        this.FindControl<TextBox>("WorkspaceNameInput")!.Focus();

    private ListBox EntryListControl => this.FindControl<ListBox>("EntryList")!;

    private ListBox PeerListControl => this.FindControl<ListBox>("PeerList")!;

    private StackPanel SelectedEntryEditorControl =>
        this.FindControl<StackPanel>("SelectedEntryEditor")!;

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
        SynchronizePeerSelection();
        // After the binding pass, not during DataContextChanged: the entry
        // list's ItemsSource has not delivered the new editor's entries yet, so
        // selecting the first entry now is coerced back to nothing — which is
        // why reopening the editor used to land on the empty state.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            EnsureEntrySelection();
            SynchronizePeerSelection();
        });
    }

    private void ConfigurePickers()
    {
        var editor = Editor;
        // The add-lists live inside flyouts. Their names still resolve here:
        // Avalonia registers a flyout's content in the enclosing name scope when
        // the markup loads, not when the flyout first opens — verified rather
        // than assumed, because the opposite would be a null at every open.
        var addConnectionList = this.FindControl<ListBox>("AddConnectionList")!;
        var addScreenList = this.FindControl<ListBox>("AddScreenList")!;
        var addLayoutList = this.FindControl<ListBox>("AddLayoutList")!;
        var entryConnectionPicker = this.FindControl<ComboBox>("EntryConnectionPicker")!;
        var entryScreenPicker = this.FindControl<ComboBox>("EntryScreenPicker")!;

        if (editor is null)
        {
            addConnectionList.ItemsSource = null;
            addScreenList.ItemsSource = null;
            addLayoutList.ItemsSource = null;
            entryConnectionPicker.ItemsSource = null;
            entryScreenPicker.ItemsSource = null;
            return;
        }

        // Auto-saved layouts carry a live tab's captured geometry; they resolve
        // existing tab references but are not offered for new tabs.
        var pickableLayouts = editor.LayoutOptions
            .Where(option => !LayoutDefinition.IsAutoSaved(option.Id))
            .ToArray();
        addConnectionList.ItemsSource = editor.ConnectionOptions;
        addScreenList.ItemsSource = editor.ScreenOptions;
        addLayoutList.ItemsSource = pickableLayouts;
        entryConnectionPicker.ItemsSource = editor.ConnectionOptions;
        entryScreenPicker.ItemsSource = editor.ScreenOptions;
        addLayoutList.SelectedItem = pickableLayouts.FirstOrDefault(option => option.IsAvailable);
        SynchronizeColorPickers();
    }

    /// <summary>
    /// Selection in the icon and swatch rows is bound declaratively, so only the
    /// colour pickers — which hold their own value — need pushing.
    /// </summary>
    private void SynchronizeColorPickers()
    {
        SynchronizeAccentPicker();
        SynchronizeIdentityColorPicker();
    }

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
            SetPickerColor("AccentColorPicker", Color.Parse(ThemePreference.BronzeFallback.ToString()));
            return;
        }

        if (!Color.TryParse(editor.Accent, out var color))
        {
            return;
        }

        SetPickerColor("AccentColorPicker", color);
    }

    private void SynchronizeIdentityColorPicker()
    {
        if (Editor is not { } editor || _syncingColor)
        {
            return;
        }

        if (Color.TryParse(editor.EffectiveColor, out var color))
        {
            SetPickerColor("ColorCustomPicker", color);
        }
    }

    private void SetPickerColor(string pickerName, Color color)
    {
        var syncingAccent = _syncingAccent;
        var syncingColor = _syncingColor;
        _syncingAccent = true;
        _syncingColor = true;
        try
        {
            this.FindControl<ColorPicker>(pickerName)!.Color = color;
        }
        finally
        {
            _syncingAccent = syncingAccent;
            _syncingColor = syncingColor;
        }
    }

    private void OnIconChoiceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is { } editor
            && sender is Button { DataContext: WorkspaceIconChoiceViewModel choice })
        {
            editor.Icon = choice.Id;
        }
    }

    /// <summary>
    /// The swatch rows are bound to choice view models rather than to the palette
    /// records they wrap, because a swatch also carries whether it is the chosen
    /// one. Matching the wrong type here is silent: the click does nothing.
    /// </summary>
    private void OnColorPresetClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is { } editor
            && sender is Button { DataContext: WorkspaceAccentChoiceViewModel choice })
        {
            editor.Color = choice.Hex;
        }
    }

    private void OnAccentPresetClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is { } editor
            && sender is Button { DataContext: WorkspaceAccentChoiceViewModel choice })
        {
            editor.Accent = choice.Hex;
        }
    }

    private void OnColorChanged(object? sender, ColorChangedEventArgs e)
    {
        _ = sender;
        if (Editor is not { } editor || _syncingColor)
        {
            return;
        }

        _syncingColor = true;
        try
        {
            editor.Color = ToHex(e.NewColor);
        }
        finally
        {
            _syncingColor = false;
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
            editor.Accent = ToHex(e.NewColor);
        }
        finally
        {
            _syncingAccent = false;
        }
    }

    private void OnClearAccentClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Editor is { } editor)
        {
            editor.Accent = string.Empty;
        }
    }

    private void OnPickAccentClick(object? sender, RoutedEventArgs e) =>
        PickAccentRequested?.Invoke(sender, e);

    private void OnRenameWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var input = this.FindControl<TextBox>("WorkspaceNameInput")!;
        input.Focus();
        input.SelectAll();
    }

    private void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CreateWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Applies a colour sampled by the host. Kept separate from
    /// <see cref="OnAccentColorChanged"/> so a sample is a deliberate edit rather
    /// than a picker echo.
    /// </summary>
    public void ApplySampledAccent(Color color)
    {
        if (Editor is { } editor)
        {
            editor.Accent = ToHex(color);
        }
    }

    private void SynchronizePeerSelection()
    {
        var editor = Editor;
        _syncingPeers = true;
        try
        {
            PeerListControl.SelectedItem = editor?.Peers.FirstOrDefault(peer => peer.IsCurrent);
        }
        finally
        {
            _syncingPeers = false;
        }
    }

    private void OnPeerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (_syncingPeers
            || Editor is not { } editor
            || (sender as ListBox)?.SelectedItem is not WorkspaceRailItemViewModel peer)
        {
            return;
        }

        if (peer.Id == editor.Id)
        {
            return;
        }

        WorkspaceSelectionRequested?.Invoke(this, peer.Id);
        // The host may refuse — an invalid workspace cannot be left behind — so
        // the rail shows what is actually open rather than what was clicked.
        Avalonia.Threading.Dispatcher.UIThread.Post(SynchronizePeerSelection);
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
        if (e.PropertyName is nameof(WorkspaceEditorViewModel.Accent)
            or nameof(WorkspaceEditorViewModel.Color))
        {
            SynchronizeColorPickers();
        }
        else if (e.PropertyName == nameof(WorkspaceEditorViewModel.Entries))
        {
            EnsureEntrySelection();
        }
        else if (e.PropertyName == nameof(WorkspaceEditorViewModel.Peers))
        {
            SynchronizePeerSelection();
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
    }

    /// <summary>
    /// Reordering is a drag on the handle, not a pair of arrows: the order is
    /// launch order, and dragging is how a list of things that happen in sequence
    /// is rearranged everywhere else. The move is applied continuously so the row
    /// under the cursor is the row that will be there when the drag ends.
    /// </summary>
    private void OnEntryDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: WorkspaceEntryEditorViewModel entry })
        {
            return;
        }

        _draggingEntry = entry;
        e.Pointer.Capture(sender as IInputElement);
        EntryListControl.SelectedItem = entry;
        e.Handled = true;
    }

    private void OnEntryDragHandleMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        if (_draggingEntry is not { } entry || Editor is not { } editor)
        {
            return;
        }

        var target = EntryIndexAt(e.GetPosition(EntryListControl));
        if (target < 0 || target == editor.Entries.IndexOf(entry))
        {
            return;
        }

        _ = editor.MoveEntry(entry.Id, target);
        EntryListControl.SelectedItem = entry;
    }

    private void OnEntryDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        _draggingEntry = null;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Which row a point in the list belongs to. Containers are asked for their
    /// own bounds rather than the point being hit-tested, because the pointer is
    /// captured by the handle while dragging and hit-testing would only ever
    /// return the handle.
    /// </summary>
    private int EntryIndexAt(Point position)
    {
        for (var index = 0; index < EntryListControl.ItemCount; index++)
        {
            if (EntryListControl.ContainerFromIndex(index) is not Control container
                || container.TranslatePoint(default, EntryListControl) is not { } origin)
            {
                continue;
            }

            if (position.Y >= origin.Y && position.Y <= origin.Y + container.Bounds.Height)
            {
                return index;
            }
        }

        return -1;
    }

    private void OnAddConnectionSelected(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (sender is not ListBox list
            || list.SelectedItem is not ScreenConnectionOption option
            || Editor is not { } editor)
        {
            return;
        }

        list.SelectedItem = null;
        CloseFlyoutAround(list);
        CompleteAdd(editor.AddConnection(option.Id));
    }

    /// <summary>
    /// A screen can be linked or copied, and the difference matters later, so the
    /// list only records the choice and the flyout's two buttons decide which.
    /// </summary>
    private void OnAddSavedScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is not { } editor
            || this.FindControl<ListBox>("AddScreenList")!.SelectedItem
                is not WorkspaceScreenOption option)
        {
            ShowInteractionError("Choose an available saved screen first.");
            return;
        }

        CloseFlyoutAround(sender as Control);
        CompleteAdd(editor.AddSavedScreen(option.Id));
    }

    private void OnCopySavedScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is not { } editor
            || this.FindControl<ListBox>("AddScreenList")!.SelectedItem
                is not WorkspaceScreenOption option)
        {
            ShowInteractionError("Choose an available saved screen first.");
            return;
        }

        CloseFlyoutAround(sender as Control);
        CompleteAdd(editor.AddWorkspaceTabFromScreen(option.Id));
    }

    private void OnAddWorkspaceTabClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (Editor is not { } editor
            || this.FindControl<ListBox>("AddLayoutList")!.SelectedItem
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

        CloseFlyoutAround(sender as Control);
        CompleteAdd(result);
    }

    private static void CloseFlyoutAround(Control? control)
    {
        if (control?.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }
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
        SynchronizeColorPickers();
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
