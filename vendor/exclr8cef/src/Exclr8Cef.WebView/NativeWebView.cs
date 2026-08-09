using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Exclr8Cef.WebView;

/// <summary>
/// Avalonia control hosting an embedded Chromium browser as a native child
/// widget (NSView on macOS, HWND on Windows). Sister control to <see cref="WebView"/>:
/// same Avalonia property surface and same underlying <see cref="CefBrowser"/>
/// event/command surface, but renders via the platform's compositor (GPU
/// path) instead of off-screen → <c>WriteableBitmap</c>.
///
/// Trade-offs vs. <see cref="WebView"/>:
/// <list type="bullet">
///   <item>Faster for media / canvas / WebGL — no per-frame pixel copy</item>
///   <item>Native widget owns its pointer / keyboard / IME / cursor — no
///         forwarding needed, no IME plumbing duplicated</item>
///   <item>Avalonia rendering effects (Opacity, RenderTransform, …) only
///         affect the frame, not the embedded content (the native widget
///         is on top of Avalonia's composited layer)</item>
///   <item>Pop-up / overlay z-ordering can be awkward — Avalonia overlays
///         draw below the native widget</item>
/// </list>
///
/// Process-init requirement: the host must call
/// <see cref="Cef.InitializeForOsr"/> (or any init that enables Alloy +
/// external message pump) before the first <see cref="NativeWebView"/> is
/// laid out. Mixing this control with <see cref="WebView"/> in one app is
/// supported under that same init choice.
///
/// Linux/X11 is not yet wired in the native shim — only macOS and Windows.
/// </summary>
public class NativeWebView : NativeControlHost, IWebView
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<NativeWebView, string?>(nameof(Url), "about:blank");

    /// <summary>
    /// ARGB colour Chromium fills the embedded browser's render
    /// target with BEFORE any HTML paints. CEF's default is opaque
    /// white (<c>0xFFFFFFFF</c>) which surfaces as a brief white flash
    /// on the host NSView/HWND between attach and first page paint —
    /// noticeable on dark-themed hosts. Set this to an opaque dark
    /// colour (e.g. <c>0xFF0d1117</c>) or fully transparent
    /// (<c>0x00000000</c>) before the control's first arrange to
    /// suppress it. Read once at <c>AttachEmbeddedBrowser</c> time;
    /// changing later has no effect (CEF's
    /// <c>CefBrowserSettings.background_color</c> is creation-only).
    /// Alpha must be either 0x00 (transparent) or 0xFF (opaque);
    /// in-between values are ignored by Chromium.
    /// </summary>
    public static readonly StyledProperty<uint> BackgroundColorProperty =
        AvaloniaProperty.Register<NativeWebView, uint>(nameof(BackgroundColor), 0u);

    public uint BackgroundColor
    {
        get => GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    public static readonly DirectProperty<NativeWebView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, string>(
            nameof(Title), o => o.Title);

    public static readonly DirectProperty<NativeWebView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, bool>(
            nameof(IsLoading), o => o.IsLoading);

    public static readonly DirectProperty<NativeWebView, bool> CanGoBackProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, bool>(
            nameof(CanGoBack), o => o.CanGoBack);

    public static readonly DirectProperty<NativeWebView, bool> CanGoForwardProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, bool>(
            nameof(CanGoForward), o => o.CanGoForward);

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>
    /// Navigate the browser to the given URL. Equivalent to
    /// <c>Url = url</c> but reads as the command it is.
    /// </summary>
    public void NavigateToUrl(string url) => Url = url;

    /// <summary>
    /// Hide or show the embedded host widget without closing the
    /// underlying <see cref="CefBrowser"/>. The browser stays alive and
    /// keeps any in-flight JS / DOM state; only the native widget is
    /// hidden via <c>setHidden:</c> (mac) / <c>ShowWindow</c> (win),
    /// and Chromium gets a <c>WasHidden</c> notification so it can
    /// throttle work while we're off-screen.
    /// <para>
    /// Use this when sibling Avalonia content needs to draw on top of
    /// the browser (e.g. tab-switch reveals a sibling tab) — Avalonia's
    /// <c>ZIndex</c> / <c>Opacity</c> don't reorder or hide native
    /// children, so a no-op here would leak this NSView/HWND through
    /// every Avalonia draw.
    /// </para>
    /// No-op if the embedded host hasn't been created yet (still in
    /// the pre-arrange phase).
    /// </summary>
    public void SetEmbeddedHidden(bool hidden)
    {
        if (_hostView == IntPtr.Zero) return;
        Cef.SetEmbeddedHostHidden(_hostView, hidden);
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        private set => SetAndRaise(TitleProperty, ref _title, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    private bool _canGoBack;
    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetAndRaise(CanGoBackProperty, ref _canGoBack, value);
    }

    private bool _canGoForward;
    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetAndRaise(CanGoForwardProperty, ref _canGoForward, value);
    }

    /// <summary>
    /// The underlying tech-neutral browser. Null between control creation
    /// and the first arrange (when the native widget is attached). Hosts
    /// that need the full event/command surface bind to this directly —
    /// the control exposes only the Avalonia-friendly slice.
    /// </summary>
    public CefBrowser? Browser => _browser;

    /// <summary>Browser id (0 if not yet created or closed). Convenience.</summary>
    public int BrowserId => _browser?.Id ?? 0;

    /// <summary>
    /// Optional isolated request context (separate cookies / cache /
    /// storage from other browsers). MUST be set before the control is
    /// laid out for the first time — after the underlying
    /// <see cref="CefBrowser"/> is created, this is read-only.
    /// </summary>
    public CefRequestContext? RequestContext { get; set; }

    /// <summary>
    /// Fires once when the underlying CEF browser is fully initialized —
    /// <see cref="Browser"/> is populated AND CEF's <c>OnAfterCreated</c>
    /// has run, so calls like <c>LoadUrl</c> / <c>ExecuteJavaScript</c>
    /// actually do something (issuing them earlier silently drops them
    /// while CEF's pending about:blank load is still resolving).
    ///
    /// Late subscribers — handlers added after the browser is already
    /// ready — fire immediately on subscribe, so consumers don't have
    /// to race the timing.
    /// </summary>
    public event EventHandler? BrowserReady
    {
        add    { _browserReadyHandlers += value; if (_browserReady) value?.Invoke(this, EventArgs.Empty); }
        remove { _browserReadyHandlers -= value; }
    }
    private EventHandler? _browserReadyHandlers;
    private bool _browserReady;

    /// <summary>
    /// Fires when teardown is about to begin (Avalonia destroying the
    /// hosted native widget). Setting <see cref="BrowserClosingEventArgs.Cancel"/>
    /// = true is honoured by <see cref="NativeWebView"/> but cannot stop
    /// Avalonia's native-control destruction — it's effectively a "last
    /// chance to save state" hook, not a real veto on the embedded path.
    /// (For a true veto, intercept the host window's <c>Closing</c> event
    /// upstream.)
    /// </summary>
    public event EventHandler<BrowserClosingEventArgs>? BrowserClosing;

    /// <summary>Fires after the underlying browser has been fully closed.</summary>
    public event EventHandler? BrowserClosed;

    // Native handle returned by excef_create_embedded_host. NSView*/HWND
    // depending on platform; treated as an opaque IntPtr at this layer.
    private IntPtr _hostView;
    private CefBrowser? _browser;
    private bool _browserAttached;
    private bool _suppressUrlChange;
    private int _lastWidth;
    private int _lastHeight;

    // Avalonia's NativeControlHost expects a platform-specific handle-kind
    // string. The native shim returns the matching widget on each platform
    // — we just label it correctly. (Linux/X11 would be "XID" but the shim
    // doesn't implement it yet.)
    private static string PlatformHandleKind =>
        OperatingSystem.IsWindows() ? "HWND" :
        OperatingSystem.IsMacOS()   ? "NSView" :
        "XID";

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // Phase 1: create an empty native widget. Avalonia will parent it
        // into the visual tree before ArrangeOverride fires — at which
        // point the effective DPI / backing-scale is known and we can
        // attach the browser without it locking to a wrong DSF.
        var bounds = Bounds;
        int w = (int)bounds.Width  > 0 ? (int)bounds.Width  : 800;
        int h = (int)bounds.Height > 0 ? (int)bounds.Height : 600;
        // Pass the parent handle through: on Windows the host HWND is a
        // WS_CHILD window and cannot be created parentless.
        _hostView = Cef.CreateEmbeddedHost(parent.Handle, w, h);
        return new PlatformHandle(_hostView, PlatformHandleKind);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (_hostView == IntPtr.Zero) return size;
        int w = Math.Max(1, (int)finalSize.Width);
        int h = Math.Max(1, (int)finalSize.Height);

        if (!_browserAttached)
        {
            // Phase 2: the native widget is now parented and CEF can pick
            // up the correct backing-scale factor at browser-creation time.
            _browserAttached = true;
            _lastWidth = w;
            _lastHeight = h;
            var browser = Cef.AttachEmbeddedBrowser(_hostView, w, h, Url ?? "about:blank", RequestContext, BackgroundColor);
            if (browser is not null)
            {
                _browser = browser;
                SubscribeBrowserEvents(browser);
                // BrowserReady fires when CEF's OnAfterCreated has run —
                // see comment on the event for why this matters.
                browser.Initialized += (_, _) =>
                {
                    _browserReady = true;
                    _browserReadyHandlers?.Invoke(this, EventArgs.Empty);
                };
            }
        }
        else if (w != _lastWidth || h != _lastHeight)
        {
            _lastWidth = w;
            _lastHeight = h;
            Cef.ResizeBrowserView(_hostView, w, h);
        }
        return size;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty && !_suppressUrlChange && _browser is not null)
        {
            var newUrl = change.GetNewValue<string?>();
            if (!string.IsNullOrEmpty(newUrl)) _browser.LoadUrl(newUrl);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Avalonia is tearing down the hosted widget; close the underlying
        // browser through the same private teardown path so the
        // unsubscribe / null-out sequence stays consistent.
        Teardown();
        // Release the native widget (the create call retains it). If the
        // browser is still closing, the native side defers destruction to
        // the browser's OnBeforeClose — safe to request here either way.
        Cef.DestroyEmbeddedHost(_hostView);
        _browserAttached = false;
        _hostView = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

    // Internal teardown wired from DestroyNativeControlCore. Public
    // consumers who need to close the browser explicitly should call
    // <c>webView.Browser?.Close(force: …)</c> directly — that's the same
    // surface as every other browser operation.
    //
    // BrowserClosing fires before the close happens so the host can do
    // save-state work; Cancel doesn't actually block the native widget
    // being destroyed (Avalonia drives that — see comment on the event).
    private void Teardown()
    {
        if (_browser is null) return;
        var args = new BrowserClosingEventArgs();
        try { BrowserClosing?.Invoke(this, args); }
        catch { /* misbehaving handler doesn't wedge teardown */ }
        // We intentionally ignore args.Cancel on this path because Avalonia
        // is already destroying the hosted widget — keeping the CefBrowser
        // alive on a dead NSView/HWND would crash on next paint.
        UnsubscribeBrowserEvents(_browser);
        _browser.Close(force: true);
        _browser = null;
    }

    // ---- Browser event routing → Avalonia property updates ------------

    private void SubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged      += OnBrowserAddressChanged;
        b.TitleChanged        += OnBrowserTitleChanged;
        b.LoadingStateChanged += OnBrowserLoadingStateChanged;
        b.Closed              += OnBrowserClosed;
    }

    private void UnsubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged      -= OnBrowserAddressChanged;
        b.TitleChanged        -= OnBrowserTitleChanged;
        b.LoadingStateChanged -= OnBrowserLoadingStateChanged;
        b.Closed              -= OnBrowserClosed;
    }

    private void OnBrowserClosed(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => BrowserClosed?.Invoke(this, EventArgs.Empty));

    private void OnBrowserAddressChanged(object? sender, string url)
    {
        // Hop to UI thread; CEF fires these from its own UI thread, which
        // on our setup is the same as Avalonia's main thread when running
        // under InitializeForOsr — but we Post anyway to be defensive
        // against external-pump variants firing from elsewhere.
        Dispatcher.UIThread.Post(() =>
        {
            _suppressUrlChange = true;
            try { SetCurrentValue(UrlProperty, url); }
            finally { _suppressUrlChange = false; }
        });
    }

    private void OnBrowserTitleChanged(object? sender, string title)
        => Dispatcher.UIThread.Post(() => Title = title);

    private void OnBrowserLoadingStateChanged(object? sender, LoadingState s)
        => Dispatcher.UIThread.Post(() =>
        {
            IsLoading = s.IsLoading;
            CanGoBack = s.CanGoBack;
            CanGoForward = s.CanGoForward;
        });
}
