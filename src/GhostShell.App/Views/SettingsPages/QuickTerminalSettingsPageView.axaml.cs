using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GhostShell.App.Views.SettingsPages;

public sealed partial class QuickTerminalSettingsPageView : UserControl
{
    public QuickTerminalSettingsPageView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? SaveRequested;

    private void OnSaveQuickTerminalSettingsClick(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(sender, e);
}
