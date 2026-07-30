using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShell.App.Controls;
using GhostShell.Application;

using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class BrowserRuntimePanelView : UserControl
{
    private const string BlankAddressPlaceholder = "about:blank";

    public BrowserRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<KeyEventArgs>? AddressKeyDown;

    public event EventHandler<RoutedEventArgs>? BackRequested;

    public event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public event EventHandler<RoutedEventArgs>? ForwardRequested;

    public event EventHandler<RoutedEventArgs>? ReloadRequested;

    public event EventHandler<RoutedEventArgs>? StopRequested;

    /// <summary>
    /// Browser actions are raised with the presentation host as the sender.
    ///
    /// They used to be resolved from the row's data context, which meant the row
    /// had to point at the host — and that broke Close, which the shell resolves
    /// from the same context but needs to be the panel. One context cannot answer
    /// two questions; naming the host is how the view says which it means.
    /// </summary>
    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        AddressKeyDown?.Invoke(RuntimeBrowser, e);
    }

    private static void OnAddressGotFocus(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is TextBox addressBox)
        {
            addressBox.PlaceholderText = null;
        }
    }

    private static void OnAddressLostFocus(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is TextBox addressBox)
        {
            addressBox.PlaceholderText = BlankAddressPlaceholder;
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        BackRequested?.Invoke(RuntimeBrowser, e);
    }

    private void OnBrowserStateChanged(
        object? sender,
        BrowserStateChangedEventArgs e) =>
        BrowserStateChanged?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

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

    private void OnForwardClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        ForwardRequested?.Invoke(RuntimeBrowser, e);
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        ReloadRequested?.Invoke(RuntimeBrowser, e);
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        StopRequested?.Invoke(RuntimeBrowser, e);
    }
}
