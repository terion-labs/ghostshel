using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views;

/// <summary>
/// Asks for the password of a saved connection stored without one. Closes
/// with the typed value (possibly empty, meaning connect without a password)
/// or null on cancel.
/// </summary>
public sealed partial class DatabasePasswordPromptDialog : Window
{
    public DatabasePasswordPromptDialog()
    {
        InitializeComponent();
    }

    public DatabasePasswordPromptDialog(string connectionName)
        : this()
    {
        Title = $"{connectionName} password";
        PromptTitle.Text = $"{connectionName} password";
        Opened += (_, _) => PasswordInput.Focus();
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Close(PasswordInput.Text ?? string.Empty);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(PasswordInput.Text ?? string.Empty);
    }
}
