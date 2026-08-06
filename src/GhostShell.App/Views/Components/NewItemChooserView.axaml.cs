using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views.Components;

public sealed partial class NewItemChooserView : UserControl
{
    public static readonly StyledProperty<bool> ShowCloseActionProperty =
        AvaloniaProperty.Register<NewItemChooserView, bool>(
            nameof(ShowCloseAction),
            defaultValue: true);

    public static readonly StyledProperty<bool> ShowWorkspacesProperty =
        AvaloniaProperty.Register<NewItemChooserView, bool>(
            nameof(ShowWorkspaces),
            defaultValue: true);

    public NewItemChooserView() => InitializeComponent();

    public bool ShowCloseAction
    {
        get => GetValue(ShowCloseActionProperty);
        set => SetValue(ShowCloseActionProperty, value);
    }

    public bool ShowWorkspaces
    {
        get => GetValue(ShowWorkspacesProperty);
        set => SetValue(ShowWorkspacesProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? AddConnectionRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? NewBrowserRequested;
    public event EventHandler<RoutedEventArgs>? NewDatabaseRequested;
    public event EventHandler<RoutedEventArgs>? NewFileViewerRequested;
    public event EventHandler<RoutedEventArgs>? NewLocalTerminalRequested;
    public event EventHandler<RoutedEventArgs>? NewProcessMonitorRequested;
    public event EventHandler<RoutedEventArgs>? NewStatisticsRequested;
    public event EventHandler<RoutedEventArgs>? OpenConnectionRequested;

    public event EventHandler<RoutedEventArgs>? OpenRecentSessionRequested;
    public event EventHandler<RoutedEventArgs>? OpenScreenRequested;
    public event EventHandler<RoutedEventArgs>? OpenWorkspaceRequested;
    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;
    public event EventHandler<RoutedEventArgs>? ShowLayoutDesignerRequested;

    internal void FocusInitialAction() =>
        NewTerminalButton.Focus(NavigationMethod.Tab);

    private void OnAddConnectionClick(object? sender, RoutedEventArgs e) =>
        AddConnectionRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnNewBrowserClick(object? sender, RoutedEventArgs e) =>
        NewBrowserRequested?.Invoke(sender, e);

    private void OnNewDatabaseClick(object? sender, RoutedEventArgs e) =>
        NewDatabaseRequested?.Invoke(sender, e);

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

    private void OnOpenRecentSessionClick(object? sender, RoutedEventArgs e) =>
        OpenRecentSessionRequested?.Invoke(sender, e);

    private void OnOpenScreenClick(object? sender, RoutedEventArgs e) =>
        OpenScreenRequested?.Invoke(sender, e);

    private void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e) =>
        OpenWorkspaceRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        ShowLayoutDesignerRequested?.Invoke(sender, e);
}
