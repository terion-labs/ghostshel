using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Exclr8Cef.WebView.Demo;

public partial class EmbeddedHostWindow : Window
{
    public ObservableCollection<EventEntry> Events { get; } = new();
    private double _zoom;
    private bool _hitTestMode;

    public EmbeddedHostWindow() : this(null) { }
    public EmbeddedHostWindow(string? initialUrl)
    {
        InitializeComponent();
        DataContext = this;
        if (!string.IsNullOrEmpty(initialUrl))
        {
            BrowserView.Url = initialUrl;  // local NativeCefView — plain prop, read on first arrange
            UrlBox.Text = initialUrl;
        }
        LogEvent("init", $"window opened — {BrowserView.Url}");
        BrowserView.BrowserReady += OnBrowserReady;
    }

    private void OnBrowserReady(object? sender, EventArgs e)
    {
        if (BrowserView.Browser is not { } b) return;
        LogEvent("ready", $"CefBrowser #{b.Id} attached");
        b.AddressChanged       += (_, url) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { UrlBox.Text = url; LogEvent("url", url); });
        b.TitleChanged         += (_, t) => LogEvent("title", t);
        b.LoadingStateChanged  += (_, s) => LogEvent("loading", $"loading={s.IsLoading} back={s.CanGoBack} fwd={s.CanGoForward}");
        b.LoadStart            += (_, ev) => { if (ev.IsMainFrame) LogEvent("load-start", ev.Url); };
        b.LoadEnd              += (_, ev) => { if (ev.IsMainFrame) LogEvent("load-end", $"{ev.HttpStatusCode} {ev.Url}"); };
        b.LoadError            += (_, ev) => LogEvent("load-err", $"{ev.ErrorCode} {ev.FailedUrl}");
        b.ConsoleMessage       += (_, m) => LogEvent($"console.{m.Level.ToString().ToLower()}", m.Message);
        b.StatusMessage        += (_, s) => LogEvent("status", s);
        b.TooltipChanged       += (_, t) => { if (!string.IsNullOrEmpty(t)) LogEvent("tooltip", t); };
        b.FaviconChanged       += (_, u) => LogEvent("favicon", u);
        b.FullscreenModeChanged += (_, f) => LogEvent("fullscreen", f.ToString());
        b.DragStarted          += (_, ev) => LogEvent("drag-out", $"{ev.AllowedOperations} files={ev.FileNames.Count}");
        b.PermissionRequest    += async (_, ev) =>
        {
            LogEvent("permission", $"{ev.RequestedPermissions} from {ev.Origin}");
            var r = await JsDialogWindow.ShowAsync(this, Exclr8Cef.Cef.JsDialogType.Confirm,
                $"{ev.Origin} wants permission for: {ev.RequestedPermissions}", "");
            ev.Continue(r.Accepted ? Exclr8Cef.Cef.PermissionResult.Accept : Exclr8Cef.Cef.PermissionResult.Deny);
        };
        b.MediaAccessRequest   += async (_, ev) =>
        {
            LogEvent("media", $"{ev.RequestedPermissions} from {ev.Origin}");
            var r = await JsDialogWindow.ShowAsync(this, Exclr8Cef.Cef.JsDialogType.Confirm,
                $"{ev.Origin} wants media: {ev.RequestedPermissions}", "");
            if (r.Accepted) ev.AllowAll(); else ev.Deny();
        };
        b.CertError            += async (_, ev) =>
        {
            LogEvent("cert", $"{ev.ErrorCode} for {ev.RequestUrl}");
            var r = await JsDialogWindow.ShowAsync(this, Exclr8Cef.Cef.JsDialogType.Confirm,
                $"TLS error {ev.ErrorCode} for {ev.RequestUrl}\nProceed?", "");
            if (r.Accepted) ev.Proceed(); else ev.Cancel();
        };
        b.BeforePopup          += (_, ev) =>
        {
            LogEvent("popup", $"{ev.Disposition} → {ev.TargetUrl}");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = ev.TargetUrl, UseShellExecute = true }); }
            catch { }
        };
        b.JsInvoke             += (_, ev) => LogEvent("js-invoke", $"{ev.Method}({ev.ArgsJson})");
    }

    public void LogEvent(string category, string message)
        => Events.Add(new EventEntry(DateTime.Now.ToString("HH:mm:ss.fff"), category, message));

    private void OnClearEventsClick(object? sender, RoutedEventArgs e) => Events.Clear();

    private Exclr8Cef.CefBrowser? B => BrowserView.Browser;

    // ---- Navigation menu ----------------------------------------------------
    private void OnNavBack(object? sender, RoutedEventArgs e)    => B?.GoBack();
    private void OnNavForward(object? sender, RoutedEventArgs e) => B?.GoForward();
    private void OnNavReload(object? sender, RoutedEventArgs e)  => B?.Reload();
    private void OnNavStop(object? sender, RoutedEventArgs e)    => B?.StopLoad();
    private void OnNavGoUrl(object? sender, RoutedEventArgs e)   => B?.LoadUrl(UrlBox.Text ?? "about:blank");
    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return) { B?.LoadUrl(UrlBox.Text ?? "about:blank"); e.Handled = true; }
    }

    // ---- View menu ----------------------------------------------------------
    private void OnZoomIn(object? sender, RoutedEventArgs e)    { if (B is null) return; _zoom += 0.5; B.ZoomLevel = _zoom; LogEvent("zoom", _zoom.ToString("0.0")); }
    private void OnZoomOut(object? sender, RoutedEventArgs e)   { if (B is null) return; _zoom -= 0.5; B.ZoomLevel = _zoom; LogEvent("zoom", _zoom.ToString("0.0")); }
    private void OnZoomReset(object? sender, RoutedEventArgs e) { if (B is null) return; _zoom = 0; B.ZoomLevel = _zoom; LogEvent("zoom", "reset"); }
    private void OnDevTools(object? sender, RoutedEventArgs e)  => B?.ShowDevTools();

    // ---- Tools menu ---------------------------------------------------------
    private async void OnRunJs(object? sender, RoutedEventArgs e)
    {
        if (B is null) return;
        try
        {
            string js = "JSON.stringify({title: document.title, url: location.href, h1: document.querySelector('h1')?.textContent})";
            var result = await B.EvaluateJavaScriptAsync(js);
            LogEvent("js", result);
        }
        catch (Exception ex) { LogEvent("js-err", ex.Message); }
    }

    private async void OnSavePdf(object? sender, RoutedEventArgs e)
    {
        if (B is null) return;
        var path = Path.Combine(Path.GetTempPath(), $"exclr8cef-embedded-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        bool ok = await B.PrintToPdfAsync(path);
        LogEvent("pdf", ok ? path : "failed");
    }

    private void OnHitTestMode(object? sender, RoutedEventArgs e)
    {
        _hitTestMode = !_hitTestMode;
        HitTestMenuItem.Content = _hitTestMode ? "Hit-test mode ✓" : "Hit-test mode";
        LogEvent("hit-test", _hitTestMode ? "enabled — click in the page to probe" : "disabled");
        // Hit-test mode in embedded: we'd need to intercept clicks before
        // they reach the CEF NSView. AppKit drag/drop interception is more
        // involved than in OSR mode — leaving this as a no-op for v0.
    }

    // ---- Window menu --------------------------------------------------------
    //
    // Both buttons create another EmbeddedHostWindow in this same process —
    // there's a single CEF instance shared between all the windows. macOS
    // bundles are LaunchServices singletons, so "spawn another bundle
    // instance" via `open -n -a` doesn't reliably produce a fresh CEF
    // process; the args get funnelled to the running instance via Apple
    // Event and the spawn becomes a no-op. Single-process is the working
    // pattern (Electron / VS Code / cefclient all do this).
    private void OnNewWindow(object? sender, RoutedEventArgs e)
        => new EmbeddedHostWindow().Show();
    private void OnOpenExclr8(object? sender, RoutedEventArgs e)
        => new EmbeddedHostWindow("https://www.exclr8.co.za").Show();
}
