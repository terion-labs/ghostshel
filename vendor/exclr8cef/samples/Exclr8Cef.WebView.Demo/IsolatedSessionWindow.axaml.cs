using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Exclr8Cef;

namespace Exclr8Cef.WebView.Demo;

/// <summary>
/// Second window that runs its own <see cref="CefRequestContext"/> —
/// proves the context is isolated. Drop a cookie on httpbin /cookies/set
/// from one window and inspect the cookies from the other: they should
/// disagree, demonstrating that each window has its own jar.
/// </summary>
public partial class IsolatedSessionWindow : Window
{
    private CefRequestContext? _context;

    public IsolatedSessionWindow()
    {
        InitializeComponent();

        // Create a fresh in-memory ("incognito") context — vanishes when
        // the window closes. Pass a CachePath if you want persistent
        // isolation across runs.
        _context = Cef.CreateRequestContext();
        Browser.RequestContext = _context;
        Browser.NavigateToUrl("https://httpbin.org/cookies");
        AddressBox.Text = Browser.Url;
        Browser.PropertyChanged += (_, e) =>
        {
            if (e.Property == Exclr8Cef.WebView.WebView.UrlProperty)
                AddressBox.Text = e.NewValue as string;
        };

        Closed += (_, _) => { _context?.Dispose(); _context = null; };
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e) => Browser.Browser?.Reload();

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            var url = AddressBox.Text ?? "";
            if (!url.Contains("://")) url = "https://" + url;
            Browser.NavigateToUrl(url);
            e.Handled = true;
        }
    }
}
