using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

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
    private static readonly DataFormat<string> FileDragNativeMarkerFormat =
        DataFormat.CreateStringApplicationFormat(
            "ghostshell.file-entry-drag");
    private GridLength _visiblePreviewWidth = new(2, GridUnitType.Star);
    private FileDragCandidate? _fileDragCandidate;

    public FileRuntimePanelView()
    {
        InitializeComponent();
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
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
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
            e.Pointer,
            e);
    }

    private async void OnFilePointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
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

        var payload = new FilePanelTransferPayload(
            entries,
            FilePanelTransferOperation.Copy);
        var item = new DataTransferItem();
        item.Set(FileDragFormat, payload);
        item.Set(FileDragNativeMarkerFormat, "file-entry");
        var transfer = new DataTransfer();
        transfer.Add(item);
        try
        {
            _ = await DragDrop.DoDragDropAsync(
                candidate.TriggerEvent,
                transfer,
                DragDropEffects.Copy);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            if (DataContext is FileRuntimePanelViewModel panel)
            {
                panel.ReportValidationError("The file drag could not start.");
            }
        }
    }

    private void OnFilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        _ = e;
        _fileDragCandidate = null;
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (DataContext is FileRuntimePanelViewModel panel
            && e.DataTransfer.TryGetValue(FileDragFormat) is { } payload
            && payload.Entries.All(panel.CanReceiveTransfer))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.None;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (DataContext is not FileRuntimePanelViewModel panel
            || e.DataTransfer.TryGetValue(FileDragFormat) is not { } payload
            || payload.Entries.Count == 0
            || !payload.Entries.All(panel.CanReceiveTransfer))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        EntryTransferDropRequested?.Invoke(
            this,
            new FilePanelTransferDropEventArgs(panel, payload));
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

    private sealed record FileDragCandidate(
        ListBox Source,
        FilePanelEntry Entry,
        Point Origin,
        IPointer Pointer,
        PointerPressedEventArgs TriggerEvent);
}

public sealed record FilePanelTransferPayload(
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
    FilePanelTransferPayload payload) : EventArgs
{
    public FileRuntimePanelViewModel Destination { get; } =
        destination ?? throw new ArgumentNullException(nameof(destination));

    public FilePanelTransferPayload Payload { get; } =
        payload ?? throw new ArgumentNullException(nameof(payload));
}
