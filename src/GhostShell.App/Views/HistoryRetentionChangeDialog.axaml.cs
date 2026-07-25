using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Views;

public sealed partial class HistoryRetentionChangeDialog : Window
{
    public HistoryRetentionChangeDialog()
        : this(new HistoryRetentionOption(
            "Off",
            "Do not retain session metadata.",
            new RecentSessionRetentionPolicy(0, TimeSpan.FromDays(30))))
    {
    }

    public HistoryRetentionChangeDialog(HistoryRetentionOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        InitializeComponent();
        this.FindControl<TextBlock>("RetentionSummary")!.Text =
            $"{option.DisplayName}: {option.Description}";
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
