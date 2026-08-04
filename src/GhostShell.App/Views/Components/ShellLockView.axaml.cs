using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.Components;

/// <summary>
/// The lock screen: an opaque veil over the whole window with one PIN box.
/// Everything it shows and decides lives in
/// <see cref="ApplicationSecurityEditorViewModel"/>; this file only routes
/// the click and the Enter key.
/// </summary>
public partial class ShellLockView : UserControl
{
    public ShellLockView()
    {
        InitializeComponent();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible)
            {
                PinInput.Focus();
            }
        };
    }

    private void OnUnlockClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        TryUnlock();
    }

    private void OnPinKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryUnlock();
        }
    }

    private void TryUnlock()
    {
        if (DataContext is ApplicationSecurityEditorViewModel editor)
        {
            _ = editor.TryUnlockAsync();
        }
    }
}
