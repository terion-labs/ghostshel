using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.Application;

namespace GhostShell.App.Views;

public sealed partial class CloseConfirmationDialog : Window
{
    public CloseConfirmationDialog()
    {
        Heading = "Close active session?";
        Detail = "Closing will terminate active work.";
        SessionSummary = string.Empty;
        InitializeDialog();
    }

    public CloseConfirmationDialog(CloseScopeResult.ConfirmationRequired confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        Heading = confirmation.Scope switch
        {
            CloseScopeKind.Panel => "Close this panel?",
            CloseScopeKind.Tab => "Close this tab?",
            CloseScopeKind.Workspace => "Close this workspace?",
            CloseScopeKind.Window => "Close GhostSHELL?",
            CloseScopeKind.Session => "Close this session?",
            _ => "Close active sessions?",
        };
        // Not "has active work" — that claims more than the signal supports. The
        // terminal answers this from whether its cursor is at a shell prompt. Local
        // shells provide semantic prompt markers; SSH sessions use a conservative
        // prompt-shape fallback because libghostty cannot inspect the remote process
        // tree. When neither signal is conclusive, the honest statement is that
        // idleness could not be confirmed.
        Detail = confirmation.Sessions.Count == 1
            ? "This session could not be confirmed idle. Closing it ends the session."
            : $"{confirmation.Sessions.Count} sessions could not be confirmed idle. Closing ends them.";
        SessionSummary = string.Join(
            Environment.NewLine,
            confirmation.Sessions.Select(item => $"• {item.Title} — {item.Detail}"));
        InitializeDialog();
    }

    private void InitializeDialog()
    {
        InitializeComponent();
        DataContext = this;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    public string Heading { get; }

    public string Detail { get; }

    public string SessionSummary { get; }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close(false);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
