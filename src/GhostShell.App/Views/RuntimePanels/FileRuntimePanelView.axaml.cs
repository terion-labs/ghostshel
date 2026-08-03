using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentIcons.Common;

using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;
using GhostShell.Application;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class FileRuntimePanelView : UserControl
{
    private const double FileDragThreshold = 6;
    private const double PreviewMinimumWidth = 220;
    private const double PreviewSplitterThickness = 5;
    private static readonly DataFormat<FilePanelTransferPayload> FileDragFormat =
        DataFormat.CreateInProcessFormat<FilePanelTransferPayload>(
            "app.ghostshell.file-entry");
    private GridLength _visiblePreviewWidth = new(2, GridUnitType.Star);
    private ActiveFileDrag? _activeFileDrag;
    private FileRuntimePanelView? _activeFileDropView;
    private FileDragCandidate? _fileDragCandidate;
    private ListBoxItem? _folderDropTarget;

    public FileRuntimePanelView()
    {
        InitializeComponent();
        AddHandler(
            KeyDownEvent,
            OnPanelKeyDownTunnel,
            RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(KeyDownEvent, OnFilePanelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(
            PointerPressedEvent,
            OnFilePointerPressed,
            RoutingStrategies.Tunnel);
        AddHandler(
            PointerMovedEvent,
            OnFilePointerMoved,
            RoutingStrategies.Tunnel);
        AddHandler(
            PointerReleasedEvent,
            OnFilePointerReleased,
            RoutingStrategies.Tunnel);
        PointerCaptureLost += OnFilePointerCaptureLost;
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public event EventHandler<RoutedEventArgs>? CreateFolderRequested;

    public event EventHandler<RoutedEventArgs>? DeleteRequested;

    public event EventHandler<RoutedEventArgs>? DismissOperationIssueRequested;

    public event EventHandler<RoutedEventArgs>? DownloadRequested;

    public event EventHandler<TappedEventArgs>? EntryDoubleTapped;

    public event EventHandler<SelectionChangedEventArgs>? EntrySelectionChanged;

    public event EventHandler<FilePanelTransferKeyEventArgs>?
        EntryTransferKeyRequested;

    public event EventHandler<FilePanelTransferDropEventArgs>?
        EntryTransferDropRequested;

    public event EventHandler<KeyEventArgs>? LocationKeyDown;

    public event EventHandler<RoutedEventArgs>? LoadMoreRequested;

    public event EventHandler<RoutedEventArgs>? NavigateUpRequested;

    public event EventHandler<RoutedEventArgs>? OpenExternallyRequested;

    public event EventHandler<RoutedEventArgs>? RefreshRequested;

    public event EventHandler<RoutedEventArgs>? RenameRequested;

    public event EventHandler<RoutedEventArgs>? TransferRequested;

    public event EventHandler<RoutedEventArgs>? UploadRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e) =>
        ConnectionSelected?.Invoke(this, e);

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e) =>
        NewConnectionRequested?.Invoke(this, e);

    private void OnSplitLeftRightClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        SplitRequested?.Invoke(sender, PanelSplitOrientation.LeftRight);
    }

    private void OnSplitTopBottomClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        SplitRequested?.Invoke(sender, PanelSplitOrientation.TopBottom);
    }

    private void OnCreateFolderClick(object? sender, RoutedEventArgs e) =>
        CreateFolderRequested?.Invoke(sender, e);

    private void OnDeleteClick(object? sender, RoutedEventArgs e) =>
        DeleteRequested?.Invoke(sender, e);

    private void OnDismissOperationIssueClick(object? sender, RoutedEventArgs e) =>
        DismissOperationIssueRequested?.Invoke(sender, e);

    private void OnDownloadClick(object? sender, RoutedEventArgs e) =>
        DownloadRequested?.Invoke(sender, e);

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e) =>
        EntryDoubleTapped?.Invoke(sender, e);

    private void OnPreviewDownloadClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RequestDeferredPreview();
    }

    /// <summary>
    /// Space asks for a waiting preview from anywhere in the panel: the file
    /// list keeps focus while browsing, so requiring the button to be focused
    /// would make the shortcut useless exactly when it is wanted.
    ///
    /// It must tunnel rather than bubble. A ListBox treats Space as a selection
    /// key and marks it handled, so by the time the event would reach this
    /// panel on the way up, it is already spent.
    /// </summary>
    private void OnPanelKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Space
            || e.Handled
            || DataContext is not FileRuntimePanelViewModel { ShowPreviewDownloadPrompt: true })
        {
            return;
        }

        // Space is a character wherever text is being typed, and a shortcut
        // only outside those.
        if (e.Source is TextBox || IsWithinTextInput(e.Source as Visual))
        {
            return;
        }

        e.Handled = true;
        RequestDeferredPreview();
    }

    private static bool IsWithinTextInput(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TextBox)
            {
                return true;
            }
        }

        return false;
    }

    private void RequestDeferredPreview()
    {
        if (DataContext is FileRuntimePanelViewModel panel)
        {
            _ = panel.PreviewDeferredAsync();
        }
    }

    private void OnEntrySelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        EntrySelectionChanged?.Invoke(sender, e);

    private void OnLoadMoreClick(object? sender, RoutedEventArgs e) =>
        LoadMoreRequested?.Invoke(sender, e);

    private void OnLocationKeyDown(object? sender, KeyEventArgs e) =>
        LocationKeyDown?.Invoke(sender, e);

    private void OnNavigateUpClick(object? sender, RoutedEventArgs e) =>
        NavigateUpRequested?.Invoke(sender, e);

    private void OnOpenExternallyClick(object? sender, RoutedEventArgs e) =>
        OpenExternallyRequested?.Invoke(sender, e);

    private void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(sender, e);

    private void OnRenameClick(object? sender, RoutedEventArgs e) =>
        RenameRequested?.Invoke(sender, e);

    private void OnTransferClick(object? sender, RoutedEventArgs e) =>
        TransferRequested?.Invoke(sender, e);

    private void OnTogglePreviewClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not FileRuntimePanelViewModel panel)
        {
            return;
        }

        var splitterColumn = FileContentGrid.ColumnDefinitions[1];
        var previewColumn = FileContentGrid.ColumnDefinitions[2];
        if (panel.IsPreviewVisible)
        {
            if (previewColumn.Width.Value > 0)
            {
                _visiblePreviewWidth = previewColumn.Width;
            }

            previewColumn.MinWidth = 0;
            previewColumn.Width = new GridLength(0);
            splitterColumn.Width = new GridLength(0);
            panel.IsPreviewVisible = false;
            return;
        }

        splitterColumn.Width = new GridLength(PreviewSplitterThickness);
        previewColumn.Width = _visiblePreviewWidth;
        previewColumn.MinWidth = PreviewMinimumWidth;
        panel.IsPreviewVisible = true;
    }

    private void OnUploadClick(object? sender, RoutedEventArgs e) =>
        UploadRequested?.Invoke(sender, e);

    private void OnFilePanelKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Source is not Control source
            || FindFileList(source) is not { } list
            || !HasTransferModifier(e.KeyModifiers)
            || e.Key is not (Key.C or Key.X or Key.V))
        {
            return;
        }

        var entries = SelectedEntries(list);
        if (e.Key is Key.C or Key.X && entries.Count == 0)
        {
            return;
        }

        EntryTransferKeyRequested?.Invoke(
            list,
            new FilePanelTransferKeyEventArgs(entries, e));
    }

    private void OnFilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        if (e.Source is not Control source
            || FindFileList(source) is not { } list
            || source.FindAncestorOfType<ListBoxItem>()?.DataContext
                is not FileEntryViewModel entry)
        {
            return;
        }

        var point = e.GetCurrentPoint(list);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _fileDragCandidate = new FileDragCandidate(
            list,
            entry.Entry,
            point.Position,
            e.Pointer);
    }

    private void OnFilePointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        if (_activeFileDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            var current = e.GetCurrentPoint(this);
            if (!current.Properties.IsLeftButtonPressed)
            {
                CancelActiveFileDrag(active.Pointer);
                return;
            }

            UpdateActiveFileDrag(e, active);
            e.Handled = true;
            return;
        }

        if (_fileDragCandidate is not { } candidate
            || !ReferenceEquals(e.Pointer, candidate.Pointer))
        {
            return;
        }

        var point = e.GetCurrentPoint(candidate.Source);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _fileDragCandidate = null;
            return;
        }

        var delta = point.Position - candidate.Origin;
        if (Math.Abs(delta.X) < FileDragThreshold
            && Math.Abs(delta.Y) < FileDragThreshold)
        {
            return;
        }

        _fileDragCandidate = null;
        var entries = SelectedEntries(candidate.Source);
        if (!entries.Contains(candidate.Entry))
        {
            entries = [candidate.Entry];
        }

        if (DataContext is not FileRuntimePanelViewModel panel)
        {
            return;
        }

        var payload = new FilePanelTransferPayload(
            panel.Id,
            entries,
            FilePanelTransferOperation.Copy);
        var window = TopLevel.GetTopLevel(this) as MainWindow;
        if (window is null)
        {
            return;
        }

        var activeDrag = new ActiveFileDrag(
            candidate.Pointer,
            payload,
            CreateDragGhostPayload(entries, panel.ConnectionDisplayName));
        // Changing capture can synchronously raise capture-lost for the list
        // row. Establish the drag only after that old capture has unwound.
        candidate.Pointer.Capture(this);
        _activeFileDrag = activeDrag;
        window.ShowDragGhost(activeDrag.Ghost, e.GetPosition(window));
        UpdateActiveFileDrag(e, activeDrag);
        e.Handled = true;
    }

    private void OnFilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        if (_activeFileDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            CompleteActiveFileDrag(e, active);
            e.Handled = true;
            return;
        }

        _fileDragCandidate = null;
    }

    private void OnFilePointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_activeFileDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            CancelActiveFileDrag(active.Pointer, releaseCapture: false);
        }
    }

    private void UpdateActiveFileDrag(
        PointerEventArgs e,
        ActiveFileDrag active)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window)
        {
            CancelActiveFileDrag(active.Pointer);
            return;
        }

        var position = e.GetPosition(window);
        window.MoveDragGhost(position);
        var target = ResolveInternalFileDropTarget(window, position, active.Payload);
        if (!ReferenceEquals(_activeFileDropView, target?.View))
        {
            _activeFileDropView?.ClearTransferDropTarget();
            _activeFileDropView = target?.View;
        }

        if (target is { } resolved)
        {
            resolved.View.SetTransferDropTarget(resolved.Target.Folder);
        }
        else
        {
            _activeFileDropView?.ClearTransferDropTarget();
            _activeFileDropView = null;
        }
    }

    private void CompleteActiveFileDrag(
        PointerReleasedEventArgs e,
        ActiveFileDrag active)
    {
        var window = TopLevel.GetTopLevel(this) as MainWindow;
        var target = window is null
            ? null
            : ResolveInternalFileDropTarget(
                window,
                e.GetPosition(window),
                active.Payload);

        _activeFileDrag = null;
        _fileDragCandidate = null;
        _activeFileDropView?.ClearTransferDropTarget();
        _activeFileDropView = null;
        active.Pointer.Capture(null);
        window?.HideDragGhost();

        if (target is not { } resolved)
        {
            return;
        }

        resolved.View.EntryTransferDropRequested?.Invoke(
            resolved.View,
            new FilePanelTransferDropEventArgs(
                resolved.Target.Panel,
                resolved.Target.Payload,
                resolved.Target.DestinationFolder));
    }

    private void CancelActiveFileDrag(
        IPointer pointer,
        bool releaseCapture = true)
    {
        _activeFileDrag = null;
        _fileDragCandidate = null;
        _activeFileDropView?.ClearTransferDropTarget();
        _activeFileDropView = null;
        if (releaseCapture)
        {
            pointer.Capture(null);
        }

        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.HideDragGhost();
        }
    }

    private static InternalFileDropTarget? ResolveInternalFileDropTarget(
        MainWindow window,
        Point position,
        FilePanelTransferPayload payload)
    {
        if (window.InputHitTest(position) is not Control hit)
        {
            return null;
        }

        var view = hit as FileRuntimePanelView
            ?? hit.FindAncestorOfType<FileRuntimePanelView>();
        var target = view?.ResolveFileDropTarget(hit, payload);
        return view is not null && target is not null
            ? new InternalFileDropTarget(view, target)
            : null;
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (ResolveFileDropTarget(e.Source, e.DataTransfer) is { } target)
        {
            SetTransferDropTarget(target.Folder);
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        ClearTransferDropTarget();
        e.DragEffects = DragDropEffects.None;
    }

    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (Bounds.Contains(e.GetPosition(this)))
        {
            return;
        }

        ClearTransferDropTarget();
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        var target = ResolveFileDropTarget(e.Source, e.DataTransfer);
        ClearTransferDropTarget();
        if (target is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        EntryTransferDropRequested?.Invoke(
            this,
            new FilePanelTransferDropEventArgs(
                target.Panel,
                target.Payload,
                target.DestinationFolder));
    }

    private void SetTransferDropTarget(ListBoxItem? folder)
    {
        TransferDropOutline.IsVisible = folder is null;
        if (ReferenceEquals(_folderDropTarget, folder))
        {
            return;
        }

        _folderDropTarget?.Classes.Remove("transferDropTarget");
        _folderDropTarget = folder;
        _folderDropTarget?.Classes.Add("transferDropTarget");
    }

    private void ClearTransferDropTarget()
    {
        TransferDropOutline.IsVisible = false;
        _folderDropTarget?.Classes.Remove("transferDropTarget");
        _folderDropTarget = null;
    }

    private FileDropTarget? ResolveFileDropTarget(
        object? source,
        IDataTransfer dataTransfer)
    {
        return dataTransfer.TryGetValue(FileDragFormat) is { } payload
            ? ResolveFileDropTarget(source, payload)
            : null;
    }

    private FileDropTarget? ResolveFileDropTarget(
        object? source,
        FilePanelTransferPayload payload)
    {
        if (DataContext is not FileRuntimePanelViewModel panel
            || payload.Entries.Count == 0)
        {
            return null;
        }

        var folder = FindDirectoryDropTarget(source);
        var destinationFolder = folder?.DataContext is FileEntryViewModel folderEntry
            ? folderEntry.Entry.Location
            : null;

        // The panel background represents its current folder. Returning a drag
        // there to its source panel cannot change ownership or location.
        if (payload.SourcePanelId == panel.Id && destinationFolder is null)
        {
            return null;
        }

        return payload.Entries.All(entry =>
                panel.CanReceiveTransfer(entry, destinationFolder))
            ? new FileDropTarget(panel, payload, folder, destinationFolder)
            : null;
    }

    private static ListBoxItem? FindDirectoryDropTarget(object? source)
    {
        if (source is not Control control)
        {
            return null;
        }

        var item = control as ListBoxItem
            ?? control.FindAncestorOfType<ListBoxItem>();
        return item?.DataContext is FileEntryViewModel { IsDirectory: true }
            ? item
            : null;
    }

    private static ListBox? FindFileList(Control source) =>
        source as ListBox ?? source.FindAncestorOfType<ListBox>();

    private static IReadOnlyList<FilePanelEntry> SelectedEntries(ListBox list) =>
        list.SelectedItems?
            .OfType<FileEntryViewModel>()
            .Select(item => item.Entry)
            .ToArray()
        ?? [];

    private static bool HasTransferModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Meta)
        || modifiers.HasFlag(KeyModifiers.Control);

    private static DragGhostPayload CreateDragGhostPayload(
        IReadOnlyList<FilePanelEntry> entries,
        string source)
    {
        var title = entries.Count == 1
            ? entries[0].Name
            : $"{entries.Count} items";
        var symbol = entries.Count == 1
            && entries[0].Kind == FilePanelEntryKind.Directory
            ? Symbol.Folder
            : Symbol.DocumentMultiple;
        return new DragGhostPayload(symbol, title, $"From {source}");
    }

    private sealed record FileDropTarget(
        FileRuntimePanelViewModel Panel,
        FilePanelTransferPayload Payload,
        ListBoxItem? Folder,
        FilePanelLocation? DestinationFolder);

    private sealed record InternalFileDropTarget(
        FileRuntimePanelView View,
        FileDropTarget Target);

    private sealed record ActiveFileDrag(
        IPointer Pointer,
        FilePanelTransferPayload Payload,
        DragGhostPayload Ghost);

    private sealed record FileDragCandidate(
        ListBox Source,
        FilePanelEntry Entry,
        Point Origin,
        IPointer Pointer);
}

public sealed record FilePanelTransferPayload(
    GhostShell.Core.PanelInstanceId SourcePanelId,
    IReadOnlyList<FilePanelEntry> Entries,
    FilePanelTransferOperation Operation);

public sealed class FilePanelTransferKeyEventArgs(
    IReadOnlyList<FilePanelEntry> entries,
    KeyEventArgs keyEvent) : EventArgs
{
    public IReadOnlyList<FilePanelEntry> Entries { get; } =
        entries ?? throw new ArgumentNullException(nameof(entries));

    public KeyEventArgs KeyEvent { get; } =
        keyEvent ?? throw new ArgumentNullException(nameof(keyEvent));
}

public sealed class FilePanelTransferDropEventArgs(
    FileRuntimePanelViewModel destination,
    FilePanelTransferPayload payload,
    FilePanelLocation? destinationFolder) : EventArgs
{
    public FileRuntimePanelViewModel Destination { get; } =
        destination ?? throw new ArgumentNullException(nameof(destination));

    public FilePanelTransferPayload Payload { get; } =
        payload ?? throw new ArgumentNullException(nameof(payload));

    public FilePanelLocation? DestinationFolder { get; } =
        destinationFolder;
}
