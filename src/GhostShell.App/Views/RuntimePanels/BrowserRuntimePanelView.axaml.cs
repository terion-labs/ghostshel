using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShell.App.Controls;
using GhostShell.Application;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class BrowserRuntimePanelView : UserControl
{
    public BrowserRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<KeyEventArgs>? AddressKeyDown;

    public event EventHandler<RoutedEventArgs>? BackRequested;

    public event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? ForwardRequested;

    public event EventHandler<RoutedEventArgs>? ReloadRequested;

    public event EventHandler<RoutedEventArgs>? StopRequested;

    private void OnAddressKeyDown(object? sender, KeyEventArgs e) =>
        AddressKeyDown?.Invoke(sender, e);

    private void OnBackClick(object? sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(sender, e);

    private void OnBrowserStateChanged(
        object? sender,
        BrowserStateChangedEventArgs e) =>
        BrowserStateChanged?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnForwardClick(object? sender, RoutedEventArgs e) =>
        ForwardRequested?.Invoke(sender, e);

    private void OnReloadClick(object? sender, RoutedEventArgs e) =>
        ReloadRequested?.Invoke(sender, e);

    private void OnStopClick(object? sender, RoutedEventArgs e) =>
        StopRequested?.Invoke(sender, e);
}
