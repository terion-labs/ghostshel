using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.App.Controls;

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

    public event EventHandler<RoutedEventArgs>? RetryConnectionRequested;

    public event EventHandler<TerminalSessionFailureEventArgs>? SessionInitializationFailed;

    public event EventHandler<TerminalSessionSnapshotEventArgs>? SessionSnapshotChanged;

    public event EventHandler<RoutedEventArgs>? TrustHostKeyRequested;

    private void OnApplicationKeyPressed(
        object? sender,
        NativeRendererKeyInputEventArgs e) =>
        ApplicationKeyPressed?.Invoke(sender, e);

    private void OnCancelReconnectClick(object? sender, RoutedEventArgs e) =>
        CancelReconnectRequested?.Invoke(this, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, e);

    private void OnRetryConnectionClick(object? sender, RoutedEventArgs e) =>
        RetryConnectionRequested?.Invoke(this, e);

    private void OnSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e) =>
        SessionInitializationFailed?.Invoke(sender, e);

    private void OnSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e) =>
        SessionSnapshotChanged?.Invoke(sender, e);

    private void OnTrustHostKeyClick(object? sender, RoutedEventArgs e) =>
        TrustHostKeyRequested?.Invoke(this, e);
}
