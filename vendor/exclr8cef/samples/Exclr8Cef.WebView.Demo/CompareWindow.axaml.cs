using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Exclr8Cef;

namespace Exclr8Cef.WebView.Demo;

/// <summary>
/// Two-column demo proving OSR <see cref="Exclr8Cef.WebView.WebView"/> and
/// embedded <see cref="Exclr8Cef.WebView.NativeWebView"/> coexist in one
/// process under a single CEF init (InitializeForOsr globally → both modes
/// allowed; each control picks its mode at browser-creation time).
///
/// Each pane has its own URL bar + nav buttons so the two browsers can be
/// driven independently — useful for visual comparison of OSR vs embedded
/// rendering on the same page, or for confirming events fire correctly on
/// the right browser when both are alive.
/// </summary>
public partial class CompareWindow : Window
{
    public CompareWindow()
    {
        InitializeComponent();

        // Mirror the address bars after the browsers navigate themselves
        // (e.g. via a link click). Without this the URL bar stays stale.
        OsrBrowser.BrowserReady += (_, _) =>
        {
            if (OsrBrowser.Browser is { } b)
                b.AddressChanged += (_, url) => Dispatcher.UIThread.Post(() => OsrUrlBox.Text = url);
        };
        NativeBrowser.BrowserReady += (_, _) =>
        {
            if (NativeBrowser.Browser is { } b)
                b.AddressChanged += (_, url) => Dispatcher.UIThread.Post(() => NativeUrlBox.Text = url);
        };
    }

    // ---- OSR pane handlers --------------------------------------------
    //
    // Both panes go through the underlying CefBrowser (webView.Browser) —
    // the controls deliberately don't expose toolbar conveniences. Use
    // the Url property for navigation (it routes through to LoadUrl
    // internally on the next OnPropertyChanged).

    private void OnOsrBack(object? sender, RoutedEventArgs e)    => OsrBrowser.Browser?.GoBack();
    private void OnOsrForward(object? sender, RoutedEventArgs e) => OsrBrowser.Browser?.GoForward();
    private void OnOsrReload(object? sender, RoutedEventArgs e)  => OsrBrowser.Browser?.Reload();
    private void OnOsrUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            OsrBrowser.NavigateToUrl(OsrUrlBox.Text ?? "about:blank");
            e.Handled = true;
        }
    }

    // ---- Embedded pane handlers ---------------------------------------

    private void OnNativeBack(object? sender, RoutedEventArgs e)    => NativeBrowser.Browser?.GoBack();
    private void OnNativeForward(object? sender, RoutedEventArgs e) => NativeBrowser.Browser?.GoForward();
    private void OnNativeReload(object? sender, RoutedEventArgs e)  => NativeBrowser.Browser?.Reload();
    private void OnNativeUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            NativeBrowser.NavigateToUrl(NativeUrlBox.Text ?? "about:blank");
            e.Handled = true;
        }
    }
}
