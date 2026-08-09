using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Base class for typed CDP domain clients. Lazy-subscribes to
/// <see cref="CefBrowser.DevToolsMessage"/> only when something
/// actually cares about events from this domain — so a host that
/// only ever sends commands (no event subscriptions) pays no event
/// dispatch cost.
/// </summary>
public abstract class CdpDomainClient
{
    /// <summary>The browser this client routes CDP traffic for.</summary>
    protected CefBrowser Browser { get; }

    private bool _subscribed;
    private readonly object _subscribeLock = new();

    private protected CdpDomainClient(CefBrowser browser) { Browser = browser; }

    /// <summary>
    /// Call from inside a typed event's <c>add</c> accessor to start
    /// receiving CDP events from the browser. Idempotent.
    /// </summary>
    protected void EnsureEventSubscription()
    {
        if (_subscribed) return;
        lock (_subscribeLock)
        {
            if (_subscribed) return;
            _subscribed = true;
            Browser.DevToolsMessage += OnDevToolsMessage;
        }
    }

    private void OnDevToolsMessage(object? sender, DevToolsMessageEventArgs e)
    {
        if (!e.IsEvent) return;
        var method = CdpJson.GetEventMethod(e.Json);
        if (method is null) return;
        if (!method.StartsWith(DomainName, StringComparison.Ordinal)) return;
        if (method.Length <= DomainName.Length || method[DomainName.Length] != '.') return;

        // Lazy params parse — only if a subclass decides it cares about
        // this specific method. Subclasses pass a parser delegate that
        // returns the JsonElement to keep the cost out of the dispatch
        // path for unhandled methods.
        //
        // Guarded: this runs (via the DevTools trampoline) inside an
        // [UnmanagedCallersOnly] frame, and the typed parsers throw on any
        // CDP event missing an expected field. One malformed event must
        // degrade to a dropped event, not take down the dispatch chain.
        try { DispatchEvent(method, e.Json); }
        catch { }
    }

    /// <summary>CDP domain prefix this client filters for (e.g. "Page", "Network").</summary>
    protected abstract string DomainName { get; }

    /// <summary>
    /// Dispatch a CDP event to this client's typed handlers. Subclasses
    /// switch on <paramref name="method"/> and parse params only for
    /// methods they support. <paramref name="json"/> is the raw event
    /// JSON; use <see cref="CdpJson.ParseEventParams"/> when typed
    /// access is needed.
    /// </summary>
    protected abstract void DispatchEvent(string method, string json);
}
