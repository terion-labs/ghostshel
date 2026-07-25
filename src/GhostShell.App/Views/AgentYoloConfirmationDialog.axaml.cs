using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GhostShell.App.Views;

public sealed partial class AgentYoloConfirmationDialog : Window
{
    public static readonly TimeSpan ConfirmedLifetime = TimeSpan.FromMinutes(15);

    public AgentYoloConfirmationDialog()
        : this(
            "Current terminal",
            "No exact target is available.",
            "Not reported",
            "Not reported")
    {
    }

    public AgentYoloConfirmationDialog(
        string targetTitle,
        string exactTarget,
        string connectionBoundary,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionBoundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        InitializeComponent();
        this.FindControl<TextBlock>("TargetTitle")!.Text = targetTitle;
        this.FindControl<TextBlock>("ExactTarget")!.Text = exactTarget;
        this.FindControl<TextBlock>("ConnectionBoundary")!.Text = connectionBoundary;
        this.FindControl<TextBlock>("WorkingDirectory")!.Text = workingDirectory;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.FindControl<CheckBox>("Acknowledgement")!.Focus();
    }

    private void OnAcknowledgementChanged(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.FindControl<Button>("ConfirmButton")!.IsEnabled =
            this.FindControl<CheckBox>("Acknowledgement")!.IsChecked == true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close((TimeSpan?)null);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (this.FindControl<CheckBox>("Acknowledgement")!.IsChecked == true)
        {
            Close((TimeSpan?)ConfirmedLifetime);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
