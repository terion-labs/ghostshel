using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GhostShell.App.Views.Components;

/// <summary>
/// The window's own close, minimise and zoom.
///
/// The shell draws these because it no longer has a system title bar to draw
/// them for it: that bar painted its own material across the top of the window,
/// which no fill underneath could match — measured pixel for pixel against
/// another application's, it was the same bar.
///
/// It raises rather than acts, like every other route-level control here, so
/// the window keeps ownership of what closing means.
/// </summary>
public sealed partial class WindowControlsView : UserControl
{
    public WindowControlsView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? MinimiseRequested;

    public event EventHandler<RoutedEventArgs>? ZoomRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnMinimiseClick(object? sender, RoutedEventArgs e) =>
        MinimiseRequested?.Invoke(sender, e);

    private void OnZoomClick(object? sender, RoutedEventArgs e) =>
        ZoomRequested?.Invoke(sender, e);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
