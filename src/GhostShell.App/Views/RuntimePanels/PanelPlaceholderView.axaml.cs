using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GhostShell.App.Views.RuntimePanels;

/// <summary>
/// The adapter choice for a panel that has already been placed. It owns no policy:
/// each button forwards to the shell, which creates the panel and puts it in this
/// placeholder's cell.
/// </summary>
public sealed partial class PanelPlaceholderView : UserControl
{
    public PanelPlaceholderView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? TerminalRequested;

    public event EventHandler<RoutedEventArgs>? BrowserRequested;

    public event EventHandler<RoutedEventArgs>? StatisticsRequested;

    public event EventHandler<RoutedEventArgs>? FileViewerRequested;

    public event EventHandler<RoutedEventArgs>? ProcessMonitorRequested;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnChooseTerminalClick(object? sender, RoutedEventArgs e) =>
        TerminalRequested?.Invoke(sender, e);

    private void OnChooseBrowserClick(object? sender, RoutedEventArgs e) =>
        BrowserRequested?.Invoke(sender, e);

    private void OnChooseStatisticsClick(object? sender, RoutedEventArgs e) =>
        StatisticsRequested?.Invoke(sender, e);

    private void OnChooseFileViewerClick(object? sender, RoutedEventArgs e) =>
        FileViewerRequested?.Invoke(sender, e);

    private void OnChooseProcessMonitorClick(object? sender, RoutedEventArgs e) =>
        ProcessMonitorRequested?.Invoke(sender, e);
}
