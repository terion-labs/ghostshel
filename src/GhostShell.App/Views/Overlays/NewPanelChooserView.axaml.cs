using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views.Overlays;

public sealed partial class NewPanelChooserView : UserControl
{
    public NewPanelChooserView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? AddBrowserPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddFilePanelRequested;

    public event EventHandler<RoutedEventArgs>? AddProcessMonitorPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddStatisticsPanelRequested;

    public event EventHandler<RoutedEventArgs>? AddTerminalPanelRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? ShowLayoutDesignerRequested;

    internal void FocusInitialAction() =>
        NewPanelTerminalButton.Focus(NavigationMethod.Tab);

    private void OnAddBrowserPanelClick(object? sender, RoutedEventArgs e) =>
        AddBrowserPanelRequested?.Invoke(sender, e);

    private void OnAddFilePanelClick(object? sender, RoutedEventArgs e) =>
        AddFilePanelRequested?.Invoke(sender, e);

    private void OnAddProcessMonitorPanelClick(object? sender, RoutedEventArgs e) =>
        AddProcessMonitorPanelRequested?.Invoke(sender, e);

    private void OnAddStatisticsPanelClick(object? sender, RoutedEventArgs e) =>
        AddStatisticsPanelRequested?.Invoke(sender, e);

    private void OnAddTerminalPanelClick(object? sender, RoutedEventArgs e) =>
        AddTerminalPanelRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        ShowLayoutDesignerRequested?.Invoke(sender, e);
}
