using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.App.Controls;

using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class TerminalRuntimePanelView : UserControl
{
    public TerminalRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<NativeRendererKeyInputEventArgs>? ApplicationKeyPressed;

    public event EventHandler<RoutedEventArgs>? CancelReconnectRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public event EventHandler<RoutedEventArgs>? RetryConnectionRequested;

    public event EventHandler<TerminalSessionFailureEventArgs>? SessionInitializationFailed;

    public event EventHandler<TerminalSessionSnapshotEventArgs>? SessionSnapshotChanged;

    public event EventHandler<RoutedEventArgs>? TrustHostKeyRequested;

    /// <summary>
    /// Focus reached the terminal surface itself. Avalonia cannot see focus move
    /// into a native child view, so the shell activates this panel from here
    /// instead of from a focus change it never receives.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? TerminalFocusGained;

    /// <summary>
    /// Focus reached the terminal itself, so the panel it belongs to becomes the
    /// active one — the same activation a click on the title bar performs.
    /// </summary>
    private void OnTerminalFocusGained(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        TerminalFocusGained?.Invoke(this, e);
    }

    private void OnApplicationKeyPressed(
        object? sender,
        NativeRendererKeyInputEventArgs e) =>
        ApplicationKeyPressed?.Invoke(sender, e);

    private void OnCancelReconnectClick(object? sender, RoutedEventArgs e) =>
        CancelReconnectRequested?.Invoke(sender, e);

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

    private void OnRetryConnectionClick(object? sender, RoutedEventArgs e) =>
        RetryConnectionRequested?.Invoke(sender, e);

    private void OnSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e) =>
        SessionInitializationFailed?.Invoke(sender, e);

    private void OnSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e) =>
        SessionSnapshotChanged?.Invoke(sender, e);

    private void OnTrustHostKeyClick(object? sender, RoutedEventArgs e) =>
        TrustHostKeyRequested?.Invoke(sender, e);
}
