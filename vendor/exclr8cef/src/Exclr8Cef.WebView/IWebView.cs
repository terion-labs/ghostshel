namespace Exclr8Cef.WebView;

/// <summary>
/// Common runtime contract for both shipped Exclr8CEF Avalonia controls:
/// <see cref="WebView"/> (OSR — paints into Avalonia's compositor) and
/// <see cref="NativeWebView"/> (embedded — hosts a native NSView/HWND).
///
/// <para>Scope</para>
///
/// The controls are deliberately *just the browser surface*: state that
/// XAML binds to, plus access to the underlying <see cref="CefBrowser"/>
/// for everything else. No toolbar conveniences, no DevTools shortcuts,
/// no clipboard wrappers — those belong on the consumer's host UI, which
/// drives the browser via <c>webView.Browser</c>.
///
/// <para>What this interface gives you</para>
///
/// Code-level polymorphism. A ViewModel can hold an <see cref="IWebView"/>
/// reference and call into either concrete control identically:
///
/// <code>
/// public class TabViewModel
/// {
///     public IWebView View { get; set; } = default!;
///     public void Navigate(string url) =&gt; View.NavigateToUrl(url);
///     public void Reload() =&gt; View.Browser?.Reload();
/// }
/// </code>
///
/// <para>What this interface CAN'T give you</para>
///
/// XAML polymorphism. Avalonia's <c>StyledProperty&lt;T&gt;</c> /
/// <c>DirectProperty&lt;T&gt;</c> are static fields on the concrete control
/// type, so XAML attribute bindings still target <see cref="WebView"/> or
/// <see cref="NativeWebView"/> by their concrete tag name. The interface is
/// for procedural code, not declarative XAML.
///
/// <para>Why no abstract base class</para>
///
/// The two controls derive from different Avalonia framework types
/// (<c>Avalonia.Controls.Control</c> for the OSR-rendered path,
/// <c>Avalonia.Controls.NativeControlHost</c> for the foreign-widget path).
/// C# single inheritance + the framework hierarchy mean a shared abstract
/// base isn't expressible. An interface is the lightest mechanism that
/// captures the contract without forcing either control to swap its
/// rendering model.
/// </summary>
public interface IWebView
{
    // ---- State (XAML-bindable on the concrete types) -----------------

    /// <summary>
    /// Two-way: the current document URL. The setter is provided for XAML
    /// initial-value syntax and two-way binding scenarios; it delegates to
    /// <see cref="NavigateToUrl"/>. For code-driven navigation prefer
    /// <see cref="NavigateToUrl"/> directly — it reads as the command it
    /// is (this property reads as state).
    /// </summary>
    string? Url { get; set; }

    // ---- Navigation -------------------------------------------------

    /// <summary>
    /// Navigate the browser to the given URL. Equivalent to setting the
    /// <see cref="Url"/> property, but reads as the command it is.
    ///
    /// <para>For richer navigation (POST body, custom headers, ignore-cache
    /// reload, etc.) go through <see cref="Browser"/>:
    /// <c>browser.LoadRequest("POST", url, body, headers)</c>.</para>
    /// </summary>
    void NavigateToUrl(string url);

    /// <summary>Current document title.</summary>
    string Title { get; }

    /// <summary>True while a navigation is in flight.</summary>
    bool IsLoading { get; }

    bool CanGoBack { get; }
    bool CanGoForward { get; }

    // ---- Browser surface ---------------------------------------------

    /// <summary>
    /// The underlying tech-neutral browser. Null until the first arrange
    /// creates it, and after teardown. Use for everything beyond the
    /// state-binding surface — navigation methods, DevTools, JS, console,
    /// downloads, dialogs, audio, frame capture, hit-test, accessibility,
    /// spellcheck, find-in-page, etc.
    /// </summary>
    CefBrowser? Browser { get; }

    /// <summary>Browser id (0 if not yet created or closed). Convenience.</summary>
    int BrowserId { get; }

    /// <summary>
    /// Optional isolated request context (separate cookies / cache /
    /// storage from other browsers). MUST be set before the control is
    /// laid out for the first time — after the underlying
    /// <see cref="CefBrowser"/> is created, changes are ignored.
    /// </summary>
    CefRequestContext? RequestContext { get; set; }

    // ---- Lifecycle events --------------------------------------------

    /// <summary>
    /// Fires once after the underlying browser is created and attached.
    /// Subscribe to per-browser events (<c>ConsoleMessage</c>, dialogs,
    /// downloads, etc.) on <see cref="Browser"/> in this handler.
    /// </summary>
    event EventHandler? BrowserReady;

    /// <summary>
    /// Fires when teardown is about to begin (host window closing, native
    /// widget being destroyed). Setting <see cref="BrowserClosingEventArgs.Cancel"/>
    /// = true vetoes the teardown — the browser stays alive. Useful for
    /// prompting the user, saving state, etc.
    /// </summary>
    event EventHandler<BrowserClosingEventArgs>? BrowserClosing;

    /// <summary>Fires after the underlying browser has been fully closed.</summary>
    event EventHandler? BrowserClosed;
}

/// <summary>Args for <see cref="IWebView.BrowserClosing"/>.</summary>
public sealed class BrowserClosingEventArgs : EventArgs
{
    /// <summary>
    /// Set to <c>true</c> to veto the teardown — the browser stays open.
    /// Defaults to <c>false</c> (allow teardown to proceed).
    /// </summary>
    public bool Cancel { get; set; }
}
