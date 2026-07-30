using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.Components;

public sealed partial class SavedConnectionShortcutView : UserControl
{
    public SavedConnectionShortcutView()
    {
        InitializeComponent();
    }

    public event EventHandler<SavedConnectionLaunchViewModel>? LaunchRequested;

    private void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is SavedConnectionShortcutViewModel shortcut)
        {
            LaunchRequested?.Invoke(this, shortcut.DefaultLaunch);
        }
    }

    private void OnAlternativeClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: SavedConnectionLaunchViewModel launch })
        {
            if (ShortcutMenuButton.Flyout is not null)
            {
                ShortcutMenuButton.Flyout.IsOpen = false;
            }

            LaunchRequested?.Invoke(this, launch);
        }
    }
}
