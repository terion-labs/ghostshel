using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Exclr8Cef;

namespace Exclr8Cef.WebView.Demo;

/// <summary>
/// Embeds an Alloy-runtime CEF browser as a native child view (NSView on
/// macOS, HWND on Windows) inside Avalonia via <see cref="NativeControlHost"/>.
/// Renders without OSR — CEF paints directly into the platform window.
///
/// Limitations:
/// - Alloy runtime only (Chrome runtime can't be embedded).
/// - macOS + Windows supported; Linux/X11 needs the matching native shim
///   path (excef_create_browser_view on Linux is not yet implemented).
/// </summary>
public class NativeCefView : NativeControlHost
{
    public string? Url { get; set; }
    /// <summary>The underlying CefBrowser, available after the native control is created.</summary>
    public CefBrowser? Browser { get; private set; }
    /// <summary>Raised once after <see cref="Browser"/> is populated, so the host can subscribe to per-browser events.</summary>
    public event EventHandler? BrowserReady;
    private IntPtr _hostView;
    private bool _browserAttached;

    // Avalonia's NativeControlHost expects a platform-specific handle
    // type string. NSView for Cocoa, HWND for Win32. The shim returns
    // the matching native handle from excef_create_embedded_host on each
    // platform — we just have to label it correctly here.
    private static string PlatformHandleKind =>
        OperatingSystem.IsWindows() ? "HWND" :
        OperatingSystem.IsMacOS()   ? "NSView" :
        // Avalonia's Linux/X11 backend uses "XID". Without a Linux
        // implementation in the shim this branch will misbehave, but at
        // least the cast is the right shape if/when that lands.
        "XID";

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // Phase 1: create an empty native widget and return it. Avalonia
        // will parent it into the visual tree before our ArrangeOverride
        // fires (HiDPI / effective DPI is only known after parenting).
        var bounds = Bounds;
        int w = (int)bounds.Width  > 0 ? (int)bounds.Width  : 800;
        int h = (int)bounds.Height > 0 ? (int)bounds.Height : 600;
        _hostView = Cef.CreateEmbeddedHost(w, h);
        return new PlatformHandle(_hostView, PlatformHandleKind);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (_hostView == IntPtr.Zero) return size;
        int w = (int)finalSize.Width;
        int h = (int)finalSize.Height;
        if (w <= 0 || h <= 0) return size;

        if (!_browserAttached)
        {
            // Phase 2: now that the NSView is parented to its window, the
            // backing scale factor is known. Attach the CEF browser — the
            // page will be created at the correct DSF from the start.
            _browserAttached = true;
            Browser = Cef.AttachEmbeddedBrowser(_hostView, w, h, Url ?? "about:blank");
            if (Browser is not null) BrowserReady?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Cef.ResizeBrowserView(_hostView, w, h);
        }
        return size;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _hostView = IntPtr.Zero;
        _browserAttached = false;
        base.DestroyNativeControlCore(control);
    }
}
