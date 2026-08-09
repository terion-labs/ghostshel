using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Exclr8Cef.WebView.Demo;

public partial class AuthDialogWindow : Window
{
    private bool _accepted;

    public AuthDialogWindow() { InitializeComponent(); }

    private void OnOkClick(object? sender, RoutedEventArgs e)     { _accepted = true; Close(); }
    private void OnCancelClick(object? sender, RoutedEventArgs e) { _accepted = false; Close(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return) { _accepted = true; Close(); e.Handled = true; }
        else if (e.Key == Key.Escape)                  { _accepted = false; Close(); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    public static async Task<(string Username, string Password)?> ShowAsync(
        Window owner, string scheme, string host, int port, string realm)
    {
        var dlg = new AuthDialogWindow();
        var portStr = port == 80 || port == 443 ? "" : $":{port}";
        dlg.MessageText.Text = $"{scheme.ToUpperInvariant()} authentication required for {host}{portStr}"
                              + (string.IsNullOrEmpty(realm) ? "" : $"\nRealm: \"{realm}\"");
        dlg.UsernameBox.Loaded += (_, _) => dlg.UsernameBox.Focus();
        await dlg.ShowDialog(owner);
        if (!dlg._accepted) return null;
        return (dlg.UsernameBox.Text ?? "", dlg.PasswordBox.Text ?? "");
    }
}
