using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class FileRuntimePanelView : UserControl
{
    private const double PreviewMinimumWidth = 220;
    private const double PreviewSplitterThickness = 5;
    private GridLength _visiblePreviewWidth = new(2, GridUnitType.Star);

    public FileRuntimePanelView()
    {
        InitializeComponent();
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
}
