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
            CloseScopeKind.Window => "Close GhostSHELL?",
            CloseScopeKind.Session => "Close this session?",
            _ => "Close active sessions?",
        };
        Detail = confirmation.Sessions.Count == 1
            ? "The terminal still has active work. Closing it will terminate that process."
            : $"{confirmation.Sessions.Count} sessions still have active work. Closing will terminate those processes.";
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
