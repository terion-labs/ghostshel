using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Exclr8Cef.WebView;

/// <summary>
/// Avalonia control hosting an embedded Chromium browser via Exclr8CEF's
/// off-screen rendering path. Owns a <see cref="CefBrowser"/> instance
/// (exposed via the <see cref="Browser"/> property) — hosts that need the
/// full per-browser event surface (console messages, downloads, dialogs,
/// …) subscribe to events on <c>webView.Browser</c> directly rather than
/// duplicating each event on the control.
///
/// The control owns the Avalonia-side concerns: paint → WriteableBitmap
/// → Render(), pointer / keyboard / IME forwarding, cursor mapping. The
/// underlying browser lifecycle (creation, resize, close) is also driven
/// from here for ergonomics, but the <see cref="CefBrowser"/> instance is
/// itself tech-neutral.
/// </summary>
public class WebView : Control, IWebView, IDisposable
{
    /// <summary>
    /// Deterministically close the underlying browser. Teardown otherwise
    /// only runs from the host window's <c>Closing</c> event — a WebView
    /// removed from the visual tree and dropped (closed tab, dynamic
    /// layout) would keep its CefBrowser alive in the native registry
    /// forever, which leaks the renderer process and can hang
    /// <c>Cef.Shutdown</c>. Detach itself intentionally does NOT close
    /// (controls detach temporarily on tab switches); call Dispose when
    /// the WebView is gone for good. Ignores <see cref="BrowserClosing"/>
    /// vetoes. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_browser is not null)
        {
            UnsubscribeBrowserEvents(_browser);
            _browser.Close(force: true);
            _browser = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }
        _popupBitmap?.Dispose();
        _popupBitmap = null;
        _dragBitmap?.Dispose();
        _dragBitmap = null;
        lock (_paintGate)
        {
            if (_pendingPaint is { } pending)
            {
                ArrayPool<byte>.Shared.Return(pending.Buffer);
                _pendingPaint = null;
            }
        }
        GC.SuppressFinalize(this);
    }

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<WebView, string?>(nameof(Url), "about:blank");

    public static readonly DirectProperty<WebView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<WebView, string>(
            nameof(Title), o => o.Title);

    public static readonly DirectProperty<WebView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(
            nameof(IsLoading), o => o.IsLoading);

    public static readonly DirectProperty<WebView, bool> CanGoBackProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(
            nameof(CanGoBack), o => o.CanGoBack);

    public static readonly DirectProperty<WebView, bool> CanGoForwardProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(
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
    /// The underlying tech-neutral browser. Null until the first arrange
    /// with non-zero size creates it, and after <see cref="Close"/>.
    /// Hosts that need the full event/command surface should bind to this
    /// directly — the control exposes only Avalonia-friendly slices.
    /// </summary>
    public CefBrowser? Browser => _browser;

    /// <summary>Browser id (0 if not yet created or closed). Convenience.</summary>
    public int BrowserId => _browser?.Id ?? 0;

    /// <summary>
    /// Optional isolated request context (separate cookies / cache /
    /// storage from other browsers). MUST be set before the WebView is
    /// arranged / attached for the first time — after the underlying
    /// <see cref="CefBrowser"/> is created, this is read-only.
    /// </summary>
    public CefRequestContext? RequestContext { get; set; }

    /// <summary>
    /// Fires once the underlying CEF browser is fully initialized
    /// (CEF's <c>OnAfterCreated</c> has run and the browser is safe to
    /// call). Subscribe per-browser events
    /// (<c>ConsoleMessage</c>, <c>FileDialog</c>, …) here, and issue any
    /// programmatic <c>NavigateToUrl</c> / <c>LoadRequest</c> / DevTools
    /// calls.
    ///
    /// <para>Late-subscribe friendly: if the browser is already
    /// initialized when you subscribe, your handler is invoked
    /// synchronously on the subscribing thread.</para>
    /// </summary>
    public event EventHandler? BrowserReady
    {
        add { _browserReadyHandlers += value; if (_browserReady) value?.Invoke(this, EventArgs.Empty); }
        remove { _browserReadyHandlers -= value; }
    }
    private EventHandler? _browserReadyHandlers;
    private bool _browserReady;

    /// <summary>
    /// Fires when teardown is about to begin (host window closing).
    /// Setting <see cref="BrowserClosingEventArgs.Cancel"/> = true vetoes
    /// it — the browser stays alive. Useful for save-state prompts.
    /// </summary>
    public event EventHandler<BrowserClosingEventArgs>? BrowserClosing;

    /// <summary>Fires after the underlying browser has been fully closed.</summary>
    public event EventHandler? BrowserClosed;

    private CefBrowser? _browser;
    // Browser dimensions in DIPs / CSS pixels. The native shim multiplies
    // these by _renderScale (passed via SetDeviceScaleFactor + the create
    // call) to get the physical-pixel paint buffer size.
    private int _browserWidth;
    private int _browserHeight;
    private double _renderScale = 1.0;
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    // Popup overlay state — <select> dropdowns, autocomplete, etc.
    private WriteableBitmap? _popupBitmap;
    private int _popupBitmapWidth;
    private int _popupBitmapHeight;
    private bool _popupVisible;
    // Popup rect in DIP / CSS pixels relative to the browser's main view.
    private int _popupX, _popupY, _popupW, _popupH;

    // Drag-preview overlay state — the bitmap CEF gave us at StartDragging.
    // Drawn under the cursor (offset by hotspot) until the drag ends.
    private WriteableBitmap? _dragBitmap;
    private int _dragBitmapWidthPx;   // physical pixels, may differ from DIP size
    private int _dragBitmapHeightPx;
    private int _dragHotspotX, _dragHotspotY;
    private int _dragCursorX, _dragCursorY;   // last pointer position in DIPs
    private bool _dragOverlayVisible;
    private bool _attached;
    private bool _suppressUrlChange;
    private WebViewTextInputMethodClient? _imeClient;
    private Window? _hostedWindow;
    private readonly object _paintGate = new();
    private PendingPaint? _pendingPaint;
    private bool _paintDispatchScheduled;
    private bool _disposed;

    public WebView()
    {
        ClipToBounds = true;
        Focusable = true;

        // Disable Avalonia's tab-navigation involvement on the WebView. Tab
        // key handling belongs to the embedded Chromium page; without this,
        // Avalonia's KeyboardNavigationHandler ALSO processes Tab.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);

        TextInputMethodClientRequested += (_, e) =>
        {
            _imeClient ??= new WebViewTextInputMethodClient(this);
            e.Client = _imeClient;
        };

        // KeyDown forwarding runs in the Tunnel phase so we claim the event
        // (for Tab, Enter, etc.) before any class handler — chiefly
        // KeyboardNavigationHandler — also processes it.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Drag-drop: forward OS-level drags into CEF so the page sees the
        // drag-over / drop events. Setting AllowDrop here covers consumers
        // that just want files-into-the-page to Just Work.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnAvDragEnter);
        AddHandler(DragDrop.DragOverEvent,  OnAvDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnAvDragLeave);
        AddHandler(DragDrop.DropEvent,      OnAvDrop);
    }

    // ---- Drag-drop forwarding (Avalonia → CEF) -------------------------

    private static Cef.DragOperations ToCefOps(DragDropEffects e)
    {
        var ops = Cef.DragOperations.None;
        if ((e & DragDropEffects.Copy) != 0) ops |= Cef.DragOperations.Copy;
        if ((e & DragDropEffects.Move) != 0) ops |= Cef.DragOperations.Move;
        if ((e & DragDropEffects.Link) != 0) ops |= Cef.DragOperations.Link;
        return ops == Cef.DragOperations.None ? Cef.DragOperations.Every : ops;
    }

    private (int x, int y) DragPoint(DragEventArgs e)
    {
        var p = e.GetPosition(this);
        return ((int)p.X, (int)p.Y);
    }

    private void OnAvDragEnter(object? sender, DragEventArgs e)
    {
        if (_browser is null) return;
        var (x, y) = DragPoint(e);
        // Avalonia 12: e.Data → e.DataTransfer, DataFormats → DataFormat,
        // Contains via Formats.Contains, sync TryGetText / GetFiles.
        var dt = e.DataTransfer;
        string? text = dt.Formats.Contains(DataFormat.Text) ? dt.TryGetText() : null;
        IReadOnlyList<string>? files = null;
        if (dt.Formats.Contains(DataFormat.File))
        {
            var items = dt.TryGetFiles();
            if (items is not null)
            {
                var list = new List<string>();
                foreach (var f in items)
                {
                    var path = f.TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path)) list.Add(path);
                }
                if (list.Count > 0) files = list;
            }
        }
        _browser.DragTargetEnter(x, y, Cef.CefModifiers.None,
            ToCefOps(e.DragEffects), text: text, filePaths: files);
    }

    private void OnAvDragOver(object? sender, DragEventArgs e)
    {
        if (_browser is null) return;
        var (x, y) = DragPoint(e);
        _browser.DragTargetOver(x, y, Cef.CefModifiers.None, ToCefOps(e.DragEffects));
    }

    private void OnAvDragLeave(object? sender, RoutedEventArgs e)
    {
        _browser?.DragTargetLeave();
    }

    private void OnAvDrop(object? sender, DragEventArgs e)
    {
        if (_browser is null) return;
        var (x, y) = DragPoint(e);
        _browser.DragTargetDrop(x, y, Cef.CefModifiers.None);
    }

    // Set when OnKeyDownTunnel forwards a RawKeyDown to the browser; cleared
    // by OnKeyUp. OnTextInput consults this to decide whether to synthesize a
    // RawKeyDown ahead of its Char dispatch — required on macOS, where
    // Avalonia routes printable keys through the text-input system only,
    // never firing KeyDownEvent. Without a matching RawKeyDown the renderer
    // can't run keydown-anchored default actions (button-active-on-Space, …).
    private bool _keyDownForwarded;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_browser is null) return;

        ForwardKeyToBrowser(e, isKeyUp: false);
        _keyDownForwarded = true;

        // Only claim Handled for keys whose entire behavior is the
        // RawKeyDown's default action (nav, function keys, Cmd shortcuts).
        // For printable keys with modifiers (e.g. Shift+letter) we MUST
        // leave Handled=false so Avalonia continues to its text-input
        // pipeline (interpretKeyEvents on macOS) and OnTextInput fires.
        var accelMod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        bool isCmdShortcut = (e.KeyModifiers & accelMod) != 0;
        if (isCmdShortcut || IsNavigationKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private static bool IsNavigationKey(Key k) => k switch
    {
        Key.Tab or Key.Return or Key.Enter or Key.Escape => true,
        Key.Up or Key.Down or Key.Left or Key.Right => true,
        Key.Home or Key.End or Key.PageUp or Key.PageDown => true,
        Key.Delete or Key.Back or Key.Insert => true,
        >= Key.F1 and <= Key.F24 => true,
        _ => false,
    };

    private void ForwardKeyToBrowser(KeyEventArgs e, bool isKeyUp)
    {
        if (_browser is null) return;

        // Cmd / Ctrl shortcuts (zoom, clipboard) — handle here so they don't
        // go to CEF as ordinary key events.
        var accelMod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (!isKeyUp && (e.KeyModifiers & accelMod) != 0)
        {
            bool shiftAccel = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:        _browser.ZoomLevel += 0.5; return;
                case Key.OemMinus:
                case Key.Subtract:   _browser.ZoomLevel -= 0.5; return;
                case Key.D0:
                case Key.NumPad0:    _browser.ZoomLevel = 0;    return;
                case Key.C:          _browser.Copy();      return;
                case Key.V:          _browser.Paste();     return;
                case Key.X:          _browser.Cut();       return;
                case Key.A:          _browser.SelectAll(); return;
                case Key.Z:
                    if (shiftAccel) _browser.Redo(); else _browser.Undo();
                    return;
                case Key.Y:          _browser.Redo();      return;
            }
        }

        int vk = KeyMap.AvaloniaToWindowsVK(e.Key);
        int nativeCode = OperatingSystem.IsMacOS() ? KeyMap.AvaloniaToMacKeyCode(e.Key) : 0;
        if (nativeCode < 0) nativeCode = 0;
        var modifiers = InputMapping.MapModifiers(e.KeyModifiers);

        bool shifted = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        char keyChar = e.Key switch
        {
            Key.Enter  => '\r',
            Key.Space  => ' ',
            Key.Back   => '\b',
            Key.Escape => (char)27,
            _ => '\0',
        };
        if (keyChar == '\0')
        {
            if (vk >= 0x41 && vk <= 0x5A)
                keyChar = (char)(shifted ? vk : vk + 0x20);
            else if (vk >= 0x30 && vk <= 0x39 && !shifted)
                keyChar = (char)vk;
            else if (e.KeySymbol is { Length: > 0 } s && !char.IsControl(s[0]))
                keyChar = s[0];
        }

        _browser.SendKeyEvent(
            isKeyUp ? Cef.CefKeyEventType.KeyUp : Cef.CefKeyEventType.RawKeyDown,
            windowsKeyCode: vk, nativeKeyCode: nativeCode,
            modifiers: modifiers,
            character: keyChar, unmodifiedCharacter: keyChar,
            isSystemKey: false);

        // Enter needs a follow-up Char event for the renderer to dispatch a
        // keypress and run HTMLInputElement::defaultEventHandler — that's
        // what triggers form submission / button click. RawKeyDown alone
        // fires keydown but does NOT run the input's default action.
        if (!isKeyUp && e.Key == Key.Enter)
        {
            _browser.SendKeyEvent(Cef.CefKeyEventType.Char,
                windowsKeyCode: 0x0D, nativeKeyCode: nativeCode,
                modifiers: modifiers,
                character: '\r', unmodifiedCharacter: '\r',
                isSystemKey: false);
        }
    }

    // ---- OSR paint-pipeline internals (control-owned, not browser) ----

    /// <summary>
    /// Drop the cached paint bitmap and request a redraw. Useful when the
    /// control's bounds are about to change drastically and showing a brief
    /// black frame is preferable to a stretched / squished old bitmap until
    /// CEF's next paint lands. OSR-only — there is no equivalent on
    /// <see cref="NativeWebView"/> because the native widget owns its
    /// paint surface.
    /// </summary>
    public void InvalidateBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        InvalidateVisual();
    }

    // Internal teardown wired from the host-window close handler. Public
    // consumers who need to close the browser explicitly should call
    // <c>webView.Browser?.Close(force: …)</c> directly — that's the same
    // surface as every other browser operation.
    //
    // Returns true if teardown ran; false if a BrowserClosing handler
    // vetoed it (so the caller can keep the host window open).
    private bool Teardown()
    {
        if (_browser is null) return true;  // already torn down — fine to "close"
        var args = new BrowserClosingEventArgs();
        try { BrowserClosing?.Invoke(this, args); }
        catch { /* a misbehaving handler doesn't get to wedge teardown */ }
        if (args.Cancel) return false;
        UnsubscribeBrowserEvents(_browser);
        _browser.Close(force: true);
        _browser = null;
        _bitmap?.Dispose();
        _bitmap = null;
        return true;
    }

    // ---- Avalonia integration ------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_disposed) return;
        _attached = true;
        _browser?.WasHidden(false);

        // Avalonia 12 changed VisualTreeAttachmentEventArgs.Root's
        // return type (the old IRenderRoot getter is gone), so binaries
        // compiled against 11.x throw MissingMethodException when
        // consumed under 12. TopLevel.GetTopLevel(this) is the
        // version-agnostic way to find the hosting Window — same API
        // on Avalonia 11 and 12.
        if (TopLevel.GetTopLevel(this) is Window win)
        {
            _hostedWindow = win;
            win.Closing += OnHostWindowClosing;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _browser?.WasHidden(true);

        if (_hostedWindow is not null)
        {
            _hostedWindow.Closing -= OnHostWindowClosing;
            _hostedWindow = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // If the consumer vetoes via BrowserClosing, also cancel the
        // window's own close — otherwise we'd be left with a dead
        // WebView in a still-open window.
        if (!Teardown()) e.Cancel = true;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (!_attached || _disposed) return size;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        int w = Math.Max(1, (int)finalSize.Width);
        int h = Math.Max(1, (int)finalSize.Height);

        if (_browser is null)
        {
            _browserWidth = w;
            _browserHeight = h;
            _renderScale = scale;
            var browser = Cef.CreateOffscreenBrowser(w, h, (float)scale, Url ?? "about:blank", RequestContext);
            if (browser is not null)
            {
                _browser = browser;
                SubscribeBrowserEvents(browser);
                // BrowserReady fires when CEF's OnAfterCreated has run —
                // that's when the underlying CefBrowser ref is populated
                // and calls like LoadUrl / ExecuteJavaScript actually do
                // something. CefBrowser.Initialized is "fire now if
                // already initialized" so this is safe regardless of
                // timing.
                browser.Initialized += (_, _) =>
                {
                    _browserReady = true;
                    _browserReadyHandlers?.Invoke(this, EventArgs.Empty);
                };
            }
        }
        else
        {
            if (scale != _renderScale)
            {
                _browser.SetDeviceScaleFactor((float)scale);
                _renderScale = scale;
            }
            if (w != _browserWidth || h != _browserHeight)
            {
                _browserWidth = w;
                _browserHeight = h;
                _browser.Resize(w, h);
            }
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

    // ---- Browser event routing -----------------------------------------

    private void SubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged       += OnBrowserAddressChanged;
        b.TitleChanged         += OnBrowserTitleChanged;
        b.LoadingStateChanged  += OnBrowserLoadingStateChanged;
        b.CursorChanged        += OnBrowserCursorChanged;
        b.Painted              += OnBrowserPainted;
        b.PopupShow            += OnBrowserPopupShow;
        b.PopupSize            += OnBrowserPopupSize;
        b.PopupPainted         += OnBrowserPopupPainted;
        b.DragImage            += OnBrowserDragImage;
        b.Closed               += OnBrowserClosed;
    }

    private void UnsubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged       -= OnBrowserAddressChanged;
        b.TitleChanged         -= OnBrowserTitleChanged;
        b.LoadingStateChanged  -= OnBrowserLoadingStateChanged;
        b.CursorChanged        -= OnBrowserCursorChanged;
        b.Painted              -= OnBrowserPainted;
        b.PopupShow            -= OnBrowserPopupShow;
        b.PopupSize            -= OnBrowserPopupSize;
        b.PopupPainted         -= OnBrowserPopupPainted;
        b.DragImage            -= OnBrowserDragImage;
        b.Closed               -= OnBrowserClosed;
    }

    private void OnBrowserClosed(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => BrowserClosed?.Invoke(this, EventArgs.Empty));

    private void OnBrowserAddressChanged(object? sender, string url)
    {
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

    private void OnBrowserCursorChanged(object? sender, Cef.CefCursorType type)
    {
        // Construct the Avalonia Cursor on the UI thread — Cursor wraps a
        // platform handle that is created lazily on first use, and creating
        // it on a CEF worker thread can produce a handle that doesn't render.
        Dispatcher.UIThread.Post(() => Cursor = MapCursor(type));
    }

    // ---- Paint pipeline ------------------------------------------------

    private void OnBrowserPainted(object? sender, PaintEventArgs e)
    {
        int byteCount = e.Width * e.Height * 4;
        byte[] snapshot = ArrayPool<byte>.Shared.Rent(byteCount);
        Marshal.Copy(e.Buffer, snapshot, 0, byteCount);
        lock (_paintGate)
        {
            if (_disposed)
            {
                ArrayPool<byte>.Shared.Return(snapshot);
                return;
            }

            if (_pendingPaint is { } superseded)
            {
                ArrayPool<byte>.Shared.Return(superseded.Buffer);
            }

            _pendingPaint = new PendingPaint(
                snapshot,
                e.Width,
                e.Height,
                byteCount);
            if (_paintDispatchScheduled) return;
            _paintDispatchScheduled = true;
        }

        Dispatcher.UIThread.Post(ApplyLatestPaint, DispatcherPriority.Render);
    }

    private void ApplyLatestPaint()
    {
        PendingPaint? paint;
        lock (_paintGate)
        {
            paint = _pendingPaint;
            _pendingPaint = null;
        }

        if (paint is not null)
        {
            try
            {
                if (!_disposed && _browser is not null)
                {
                    if (_bitmap is null
                        || _bitmapWidth != paint.Width
                        || _bitmapHeight != paint.Height)
                    {
                        _bitmap?.Dispose();
                        _bitmap = new WriteableBitmap(
                            new PixelSize(paint.Width, paint.Height),
                            new Vector(96, 96),
                            PixelFormat.Bgra8888,
                            AlphaFormat.Premul);
                        _bitmapWidth = paint.Width;
                        _bitmapHeight = paint.Height;
                    }

                    using (var locked = _bitmap.Lock())
                    {
                        Marshal.Copy(
                            paint.Buffer,
                            0,
                            locked.Address,
                            paint.ByteCount);
                    }
                    InvalidateVisual();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(paint.Buffer);
            }
        }

        lock (_paintGate)
        {
            if (_pendingPaint is null || _disposed)
            {
                _paintDispatchScheduled = false;
                return;
            }
        }

        Dispatcher.UIThread.Post(ApplyLatestPaint, DispatcherPriority.Render);
    }

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
        }
        else
        {
            context.FillRectangle(Avalonia.Media.Brushes.Black, new Rect(Bounds.Size));
        }
        // Popup overlay (HTML <select> dropdowns etc.). CEF gives popup
        // coords in DIP / CSS pixels relative to the browser view origin;
        // we draw the popup bitmap at that rect, on top of the main view.
        if (_popupVisible && _popupBitmap is not null)
        {
            context.DrawImage(_popupBitmap,
                new Rect(_popupX, _popupY, _popupW, _popupH));
        }
        // Drag preview overlay (CefDragData::GetImage). Draw the bitmap at
        // the current cursor, offset by the hotspot. Bitmap is in physical
        // pixels; convert to DIPs for the destination rect.
        if (_dragOverlayVisible && _dragBitmap is not null)
        {
            double scale = _renderScale > 0 ? _renderScale : 1.0;
            double wDip = _dragBitmapWidthPx  / scale;
            double hDip = _dragBitmapHeightPx / scale;
            double hsXDip = _dragHotspotX / scale;
            double hsYDip = _dragHotspotY / scale;
            context.DrawImage(_dragBitmap,
                new Rect(_dragCursorX - hsXDip, _dragCursorY - hsYDip, wDip, hDip));
        }
    }

    private void OnBrowserDragImage(object? sender, DragImageEventArgs e)
    {
        if (e.IsClear)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _dragOverlayVisible = false;
                _dragBitmap?.Dispose();
                _dragBitmap = null;
                _dragBitmapWidthPx = _dragBitmapHeightPx = 0;
                InvalidateVisual();
            });
            return;
        }
        int byteCount = e.Width * e.Height * 4;
        byte[] snapshot = ArrayPool<byte>.Shared.Rent(byteCount);
        Marshal.Copy(e.Buffer, snapshot, 0, byteCount);
        int w = e.Width, h = e.Height, hsx = e.HotspotX, hsy = e.HotspotY;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_dragBitmap is null || _dragBitmapWidthPx != w || _dragBitmapHeightPx != h)
                {
                    _dragBitmap?.Dispose();
                    _dragBitmap = new WriteableBitmap(
                        new PixelSize(w, h),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                    _dragBitmapWidthPx = w;
                    _dragBitmapHeightPx = h;
                }
                using (var locked = _dragBitmap.Lock())
                {
                    Marshal.Copy(snapshot, 0, locked.Address, byteCount);
                }
                _dragHotspotX = hsx;
                _dragHotspotY = hsy;
                _dragOverlayVisible = true;
                InvalidateVisual();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(snapshot);
            }
        }, DispatcherPriority.Render);
    }

    private sealed record PendingPaint(
        byte[] Buffer,
        int Width,
        int Height,
        int ByteCount);

    // ---- Popup overlay handling ----------------------------------------

    private void OnBrowserPopupShow(object? sender, bool show)
        => Dispatcher.UIThread.Post(() =>
        {
            _popupVisible = show;
            if (!show)
            {
                // Drop the bitmap on hide so the next show starts fresh —
                // the popup may resize between shows.
                _popupBitmap?.Dispose();
                _popupBitmap = null;
                _popupBitmapWidth = _popupBitmapHeight = 0;
            }
            InvalidateVisual();
        });

    private void OnBrowserPopupSize(object? sender, PopupRect r)
        => Dispatcher.UIThread.Post(() =>
        {
            _popupX = r.X; _popupY = r.Y; _popupW = r.Width; _popupH = r.Height;
            InvalidateVisual();
        });

    private void OnBrowserPopupPainted(object? sender, PaintEventArgs e)
    {
        // Same staging-buffer pattern as the main view (see OnBrowserPainted).
        int byteCount = e.Width * e.Height * 4;
        byte[] snapshot = ArrayPool<byte>.Shared.Rent(byteCount);
        Marshal.Copy(e.Buffer, snapshot, 0, byteCount);
        int w = e.Width, h = e.Height;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_popupBitmap is null || _popupBitmapWidth != w || _popupBitmapHeight != h)
                {
                    _popupBitmap?.Dispose();
                    _popupBitmap = new WriteableBitmap(
                        new PixelSize(w, h),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                    _popupBitmapWidth = w;
                    _popupBitmapHeight = h;
                }
                using (var locked = _popupBitmap.Lock())
                {
                    Marshal.Copy(snapshot, 0, locked.Address, byteCount);
                }
                if (_popupVisible) InvalidateVisual();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(snapshot);
            }
        }, DispatcherPriority.Render);
    }

    // ---- Cursor cache --------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Cef.CefCursorType, Cursor> s_cursorCache = new();

    private static Cursor MapCursor(Cef.CefCursorType t) =>
        s_cursorCache.GetOrAdd(t, BuildCursor);

    private static Cursor BuildCursor(Cef.CefCursorType t) => t switch
    {
        Cef.CefCursorType.Pointer                  => new Cursor(StandardCursorType.Arrow),
        Cef.CefCursorType.Cross                    => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.Hand                     => new Cursor(StandardCursorType.Hand),
        Cef.CefCursorType.IBeam                    => new Cursor(StandardCursorType.Ibeam),
        Cef.CefCursorType.Wait                     => new Cursor(StandardCursorType.Wait),
        Cef.CefCursorType.Help                     => new Cursor(StandardCursorType.Help),
        Cef.CefCursorType.EastResize               => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthResize              => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.NorthEastResize          => new Cursor(StandardCursorType.TopRightCorner),
        Cef.CefCursorType.NorthWestResize          => new Cursor(StandardCursorType.TopLeftCorner),
        Cef.CefCursorType.SouthResize              => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.SouthEastResize          => new Cursor(StandardCursorType.BottomRightCorner),
        Cef.CefCursorType.SouthWestResize          => new Cursor(StandardCursorType.BottomLeftCorner),
        Cef.CefCursorType.WestResize               => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthSouthResize         => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.EastWestResize           => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthEastSouthWestResize => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.NorthWestSouthEastResize => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.ColumnResize             => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.RowResize                => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.MiddlePanning            => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.MiddlePanningHorizontal  => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.MiddlePanningVertical    => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.EastPanning              => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.WestPanning              => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthPanning             => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.SouthPanning             => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.NorthEastPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.NorthWestPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.SouthEastPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.SouthWestPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.Move                     => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.VerticalText             => new Cursor(StandardCursorType.Ibeam),
        Cef.CefCursorType.Cell                     => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.ContextMenu              => new Cursor(StandardCursorType.Arrow),
        Cef.CefCursorType.Alias                    => new Cursor(StandardCursorType.DragLink),
        Cef.CefCursorType.Progress                 => new Cursor(StandardCursorType.AppStarting),
        Cef.CefCursorType.NoDrop                   => new Cursor(StandardCursorType.No),
        Cef.CefCursorType.Copy                     => new Cursor(StandardCursorType.DragCopy),
        Cef.CefCursorType.None                     => new Cursor(StandardCursorType.None),
        Cef.CefCursorType.NotAllowed               => new Cursor(StandardCursorType.No),
        Cef.CefCursorType.ZoomIn                   => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.ZoomOut                  => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.Grab                     => new Cursor(StandardCursorType.Hand),
        Cef.CefCursorType.Grabbing                 => new Cursor(StandardCursorType.DragMove),
        Cef.CefCursorType.Custom                   => new Cursor(StandardCursorType.Arrow),
        Cef.CefCursorType.DndNone                  => new Cursor(StandardCursorType.No),
        Cef.CefCursorType.DndMove                  => new Cursor(StandardCursorType.DragMove),
        Cef.CefCursorType.DndCopy                  => new Cursor(StandardCursorType.DragCopy),
        Cef.CefCursorType.DndLink                  => new Cursor(StandardCursorType.DragLink),
        _                                          => new Cursor(StandardCursorType.Arrow),
    };

    // ---- Input forwarding ----------------------------------------------
    // Coordinates are in DIPs / CSS pixels.

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        int x = (int)p.Position.X, y = (int)p.Position.Y;
        if (_dragOverlayVisible)
        {
            _dragCursorX = x; _dragCursorY = y;
            InvalidateVisual();
        }
        _browser.SendMouseMove(
            x, y,
            InputMapping.MapModifiers(e.KeyModifiers, p.Properties),
            mouseLeave: false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        _browser.SendMouseMove(
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapModifiers(e.KeyModifiers, p.Properties),
            mouseLeave: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Lets a host's Tunnel-phase handler claim the click before we
        // forward to CEF (e.g. for an "inspect-mode" hit-test overlay).
        if (e.Handled) return;
        if (_browser is null) return;
        Focus();
        // Re-assert browser focus on every click. OnGotFocus only fires
        // for the initial focus transition into the control; a click that
        // moves the page-internal focus between elements doesn't trigger
        // it, leaving CEF's caret-blink stalled.
        _browser.SetFocus(true);
        var p = e.GetCurrentPoint(this);
        _browser.SendMouseClick(
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapPointerUpdateKind(p.Properties.PointerUpdateKind),
            mouseUp: false, e.ClickCount,
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        _browser.SendMouseClick(
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapInitiatingButton(e.InitialPressMouseButton),
            mouseUp: true, 1,
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        const int linePixels = 40;
        _browser.SendMouseWheel(
            (int)p.Position.X, (int)p.Position.Y,
            (int)(e.Delta.X * linePixels),
            (int)(e.Delta.Y * linePixels),
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    // Avalonia 12 unified OnGotFocus / OnLostFocus to FocusChangedEventArgs
    // (was GotFocusEventArgs + RoutedEventArgs in 11.x).
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _browser?.SetFocus(true);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_browser is not null)
        {
            _browser.ImeCancel();
            _browser.SetFocus(false);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_browser is null) return;

        // Tab moves focus on KeyDown. Sending the KeyUp afterwards makes
        // Chromium synthesize a KeyDown on the now-focused element, doubling
        // navigation. Suppress KeyUp for Tab specifically; other keys' KeyUp
        // is needed for the default action to complete (Enter→click etc.).
        if (e.Key == Key.Tab)
        {
            _keyDownForwarded = false;
            e.Handled = true;
            return;
        }

        ForwardKeyToBrowser(e, isKeyUp: true);
        _keyDownForwarded = false;
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_browser is null || string.IsNullOrEmpty(e.Text)) return;

        foreach (char c in e.Text)
        {
            // Skip control chars (Tab '\t', Enter '\r', Escape, …) — Avalonia
            // on macOS fires OnTextInput for these as well as OnKeyDown, but
            // sending both RawKeyDown and Char makes Chromium run editor
            // commands twice. We rely on OnKeyDown for those.
            if (char.IsControl(c)) continue;

            // If Avalonia didn't fire KeyDown for this key (the macOS path
            // for printable chars), synthesize a RawKeyDown so the renderer
            // pairs a keydown with the upcoming keyup. Required for default
            // actions anchored on keydown (button-active-on-Space, …).
            if (!_keyDownForwarded)
            {
                int synthVk = (c >= 'a' && c <= 'z') ? c - 32 : c;
                int synthNative = OperatingSystem.IsMacOS() ? CharToMacKeyCode(c) : 0;
                _browser.SendKeyEvent(Cef.CefKeyEventType.RawKeyDown,
                    windowsKeyCode: synthVk, nativeKeyCode: synthNative,
                    Cef.CefModifiers.None,
                    character: c, unmodifiedCharacter: c,
                    isSystemKey: false);
                _keyDownForwarded = true;
            }

            _browser.SendKeyEvent(Cef.CefKeyEventType.Char,
                windowsKeyCode: c, nativeKeyCode: 0,
                Cef.CefModifiers.None,
                character: c, unmodifiedCharacter: c,
                isSystemKey: false);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Map a printable character to its macOS Carbon HIToolbox keycode for
    /// CefKeyEvent.native_key_code. Used when synthesizing a RawKeyDown from
    /// OnTextInput; without a correct native_key_code, Chromium's
    /// NSEventKeyCodeToDomKey returns the wrong DOM <c>code</c>
    /// (e.g. <c>code=KeyA</c> for every letter, since native=0 == kVK_ANSI_A).
    /// </summary>
    private static int CharToMacKeyCode(char c)
    {
        if (c >= 'a' && c <= 'z') c = (char)(c - 32);
        return c switch
        {
            ' ' => 0x31,
            'A' => 0x00, 'B' => 0x0B, 'C' => 0x08, 'D' => 0x02,
            'E' => 0x0E, 'F' => 0x03, 'G' => 0x05, 'H' => 0x04,
            'I' => 0x22, 'J' => 0x26, 'K' => 0x28, 'L' => 0x25,
            'M' => 0x2E, 'N' => 0x2D, 'O' => 0x1F, 'P' => 0x23,
            'Q' => 0x0C, 'R' => 0x0F, 'S' => 0x01, 'T' => 0x11,
            'U' => 0x20, 'V' => 0x09, 'W' => 0x0D, 'X' => 0x07,
            'Y' => 0x10, 'Z' => 0x06,
            '0' => 0x1D, '1' => 0x12, '2' => 0x13, '3' => 0x14,
            '4' => 0x15, '5' => 0x17, '6' => 0x16, '7' => 0x1A,
            '8' => 0x1C, '9' => 0x19,
            _ => 0,
        };
    }
}
