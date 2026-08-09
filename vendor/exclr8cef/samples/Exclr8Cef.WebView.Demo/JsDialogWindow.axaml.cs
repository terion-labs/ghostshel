using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Exclr8Cef.WebView.Demo;

/// <summary>
/// Minimal host-side dialog for page-driven JS dialogs
/// (<c>alert</c> / <c>confirm</c> / <c>prompt</c> / <c>onbeforeunload</c>).
/// Returns an <see cref="JsDialogResult"/> describing the user choice and
/// (for prompts) the entered text.
/// </summary>
public partial class JsDialogWindow : Window
{
    private bool _accepted;

    public JsDialogWindow() { InitializeComponent(); }

    private void OnOkClick(object? sender, RoutedEventArgs e)     { _accepted = true; Close(); }
    private void OnCancelClick(object? sender, RoutedEventArgs e) { _accepted = false; Close(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Enter = accept, Escape = cancel (default browser-dialog ergonomics).
        if (e.Key == Key.Enter || e.Key == Key.Return) { _accepted = true; Close(); e.Handled = true; }
        else if (e.Key == Key.Escape)                  { _accepted = false; Close(); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    public static async Task<JsDialogResult> ShowAsync(
        Window owner, Exclr8Cef.Cef.JsDialogType type, string message, string defaultPromptText)
    {
        var dlg = new JsDialogWindow
        {
            Title = type switch
            {
                Exclr8Cef.Cef.JsDialogType.Alert        => "Page says…",
                Exclr8Cef.Cef.JsDialogType.Confirm      => "Page asks…",
                Exclr8Cef.Cef.JsDialogType.Prompt       => "Page prompts…",
                Exclr8Cef.Cef.JsDialogType.BeforeUnload => "Leave page?",
                _ => "Page dialog",
            },
        };
        dlg.MessageText.Text = string.IsNullOrEmpty(message) && type == Exclr8Cef.Cef.JsDialogType.BeforeUnload
            ? "Changes you made may not be saved."
            : message;

        // Alert: only OK (cancel hidden, OK = accept).
        if (type == Exclr8Cef.Cef.JsDialogType.Alert)
        {
            dlg.CancelButton.IsVisible = false;
        }
        if (type == Exclr8Cef.Cef.JsDialogType.Prompt)
        {
            dlg.PromptInput.IsVisible = true;
            dlg.PromptInput.Text = defaultPromptText;
            dlg.PromptInput.Loaded += (_, _) => { dlg.PromptInput.Focus(); dlg.PromptInput.SelectAll(); };
        }
        if (type == Exclr8Cef.Cef.JsDialogType.BeforeUnload)
        {
            dlg.OkButton.Content = "Leave";
            dlg.CancelButton.Content = "Stay";
        }

        await dlg.ShowDialog(owner);
        return new JsDialogResult(dlg._accepted, dlg._accepted && dlg.PromptInput.IsVisible
            ? dlg.PromptInput.Text ?? ""
            : null);
    }
}

public readonly record struct JsDialogResult(bool Accepted, string? PromptText);
