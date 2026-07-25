using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views.Overlays;

public sealed partial class NewItemLauncherView : UserControl
{
    public NewItemLauncherView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? AddConnectionRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? CreateScreenRequested;

    public event EventHandler<RoutedEventArgs>? CreateWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? NewBrowserRequested;

    public event EventHandler<RoutedEventArgs>? NewFileViewerRequested;

    public event EventHandler<RoutedEventArgs>? NewLocalTerminalRequested;

    public event EventHandler<RoutedEventArgs>? NewProcessMonitorRequested;

    public event EventHandler<RoutedEventArgs>? NewStatisticsRequested;

    public event EventHandler<RoutedEventArgs>? OpenConnectionRequested;

    public event EventHandler<RoutedEventArgs>? OpenScreenRequested;

    public event EventHandler<RoutedEventArgs>? OpenWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;

    public event EventHandler<RoutedEventArgs>? ShowLayoutDesignerRequested;

    internal void FocusInitialAction() =>
        NewTerminalButton.Focus(NavigationMethod.Tab);

    internal string WorkspaceName =>
        NewWorkspaceName.Text ?? string.Empty;

    internal void ClearWorkspaceName() =>
        NewWorkspaceName.Text = string.Empty;

    internal string ScreenName =>
        NewScreenName.Text ?? string.Empty;

    internal void ClearScreenName() =>
        NewScreenName.Text = string.Empty;

    private void OnAddConnectionClick(object? sender, RoutedEventArgs e) =>
        AddConnectionRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnCreateScreenClick(object? sender, RoutedEventArgs e) =>
        CreateScreenRequested?.Invoke(sender, e);

    private void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e) =>
        CreateWorkspaceRequested?.Invoke(sender, e);

    private void OnNewBrowserClick(object? sender, RoutedEventArgs e) =>
        NewBrowserRequested?.Invoke(sender, e);

    private void OnNewFileViewerClick(object? sender, RoutedEventArgs e) =>
        NewFileViewerRequested?.Invoke(sender, e);

    private void OnNewLocalTerminalClick(object? sender, RoutedEventArgs e) =>
        NewLocalTerminalRequested?.Invoke(sender, e);

    private void OnNewProcessMonitorClick(object? sender, RoutedEventArgs e) =>
        NewProcessMonitorRequested?.Invoke(sender, e);

    private void OnNewStatisticsClick(object? sender, RoutedEventArgs e) =>
        NewStatisticsRequested?.Invoke(sender, e);

    private void OnOpenConnectionClick(object? sender, RoutedEventArgs e) =>
        OpenConnectionRequested?.Invoke(sender, e);

    private void OnOpenScreenClick(object? sender, RoutedEventArgs e) =>
        OpenScreenRequested?.Invoke(sender, e);

    private void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e) =>
        OpenWorkspaceRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        ShowLayoutDesignerRequested?.Invoke(sender, e);
}
