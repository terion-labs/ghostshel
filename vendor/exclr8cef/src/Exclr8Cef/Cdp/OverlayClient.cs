using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>Overlay</c> domain — the inspector's
/// element highlighter, exposed programmatically. Lets the AI
/// visually annotate what it's about to click (great for human-in-
/// the-loop transparency in VibeCoder) or hand off click-to-pick
/// behavior to the user. Reached via <see cref="CefBrowser.Overlay"/>.
/// </summary>
public sealed class OverlayClient : CdpDomainClient
{
    internal OverlayClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "Overlay";

    /// <summary>
    /// Fires when inspect-mode is active and the user clicks an
    /// element — Chromium reports the resolved backend node id so the
    /// AI can react (look up the node in AX tree, etc.).
    /// </summary>
    public event EventHandler<InspectNodeRequestedEventArgs>? InspectNodeRequested
    {
        add { EnsureEventSubscription(); _inspect += value; }
        remove { _inspect -= value; }
    }
    private EventHandler<InspectNodeRequestedEventArgs>? _inspect;

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        if (method != "Overlay.inspectNodeRequested") return;
        if (_inspect is null) return;
        var p = CdpJson.ParseEventParams(json);
        if (!p.TryGetProperty("backendNodeId", out var b)) return;
        _inspect.Invoke(this, new InspectNodeRequestedEventArgs(b.GetInt32()));
    }

    /// <summary>
    /// <c>Overlay.enable</c>. Required before highlights or inspect
    /// mode will render. Pair with <see cref="DomClient.EnableAsync"/>.
    /// </summary>
    public Task EnableAsync()
        => Browser.ExecuteDevToolsMethodAsync("Overlay.enable", null);

    /// <summary>Disable the overlay (no more highlights, no inspect mode).</summary>
    public Task DisableAsync()
        => Browser.ExecuteDevToolsMethodAsync("Overlay.disable", null);

    /// <summary>
    /// Highlight a single node, optionally with a tooltip showing its
    /// tag / dimensions / class — same look as devtools "inspect."
    /// Call <see cref="HideHighlightAsync"/> to clear.
    /// </summary>
    /// <param name="backendNodeId">Node to highlight.</param>
    /// <param name="showInfo">Show the floating info tooltip (tag + size + classes).</param>
    /// <param name="contentColor">CSS color string for the content area (e.g. "#3aa1ff80"). Null = inspector default.</param>
    public Task HighlightNodeAsync(int backendNodeId, bool showInfo = true, string? contentColor = null)
    {
        var sb = new StringBuilder("{\"highlightConfig\":{")
            .Append("\"showInfo\":").Append(showInfo ? "true" : "false");
        if (contentColor is not null)
        {
            sb.Append(",\"contentColor\":").Append(SerializeColor(contentColor));
        }
        sb.Append("},\"backendNodeId\":").Append(backendNodeId).Append('}');
        return Browser.ExecuteDevToolsMethodAsync("Overlay.highlightNode", sb.ToString());
    }

    /// <summary>Clear any active highlight.</summary>
    public Task HideHighlightAsync()
        => Browser.ExecuteDevToolsMethodAsync("Overlay.hideHighlight", null);

    /// <summary>
    /// Enter inspect mode — Chromium displays the highlight under the
    /// pointer and fires <see cref="InspectNodeRequested"/> on click.
    /// Mode is one-shot ("searchForNode"); after the user picks an
    /// element it auto-disables.
    /// </summary>
    public Task EnterInspectModeAsync(bool showInfo = true)
    {
        var json = "{\"mode\":\"searchForNode\",\"highlightConfig\":{\"showInfo\":"
                 + (showInfo ? "true" : "false") + "}}";
        return Browser.ExecuteDevToolsMethodAsync("Overlay.setInspectMode", json);
    }

    /// <summary>
    /// Set inspect mode to <c>none</c> — exit without picking.
    /// </summary>
    public Task ExitInspectModeAsync()
        => Browser.ExecuteDevToolsMethodAsync(
            "Overlay.setInspectMode",
            "{\"mode\":\"none\",\"highlightConfig\":{}}");

    /// <summary>
    /// CDP <c>RGBA</c> color literal. Accepts either an
    /// <c>#rrggbb</c> / <c>#rrggbbaa</c> string (we'll parse it) or a
    /// fully-formed CDP RGBA JSON literal (passed through).
    /// </summary>
    private static string SerializeColor(string css)
    {
        if (css.StartsWith('{')) return css;
        if (css.StartsWith('#') && (css.Length == 7 || css.Length == 9))
        {
            int r = Convert.ToInt32(css.Substring(1, 2), 16);
            int g = Convert.ToInt32(css.Substring(3, 2), 16);
            int b = Convert.ToInt32(css.Substring(5, 2), 16);
            double a = css.Length == 9 ? Convert.ToInt32(css.Substring(7, 2), 16) / 255.0 : 1.0;
            return $"{{\"r\":{r},\"g\":{g},\"b\":{b},\"a\":{a.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}";
        }
        // Fallback — let CDP reject it.
        return JsonSerializer.Serialize(css);
    }
}

/// <summary>Args for <see cref="OverlayClient.InspectNodeRequested"/>.</summary>
public sealed class InspectNodeRequestedEventArgs : EventArgs
{
    /// <summary>Stable backend id of the node the user picked.</summary>
    public int BackendNodeId { get; }
    internal InspectNodeRequestedEventArgs(int backendNodeId) { BackendNodeId = backendNodeId; }
}
