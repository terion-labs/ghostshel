using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class FileRuntimePanelView : UserControl
{
    public FileRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

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

    public event EventHandler<SelectionChangedEventArgs>? ProfileSelectionChanged;

    public event EventHandler<RoutedEventArgs>? RefreshRequested;

    public event EventHandler<RoutedEventArgs>? RenameRequested;

    public event EventHandler<RoutedEventArgs>? TransferRequested;

    public event EventHandler<RoutedEventArgs>? UploadRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

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

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ProfileSelectionChanged?.Invoke(sender, e);

    private void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(sender, e);

    private void OnRenameClick(object? sender, RoutedEventArgs e) =>
        RenameRequested?.Invoke(sender, e);

    private void OnTransferClick(object? sender, RoutedEventArgs e) =>
        TransferRequested?.Invoke(sender, e);

    private void OnUploadClick(object? sender, RoutedEventArgs e) =>
        UploadRequested?.Invoke(sender, e);
}
