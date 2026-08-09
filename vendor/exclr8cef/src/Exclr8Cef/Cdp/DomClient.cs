using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>DOM</c> domain — reverse hit-testing,
/// element geometry, and node lookup that's substantially more
/// capable than the JS-injected <c>getBoundingClientRect</c> probe on
/// <see cref="CefBrowser.HitTestAtAsync"/>:
/// transforms / shadow DOM / <c>pointer-events</c> all handled
/// natively, and you get a stable <c>BackendNodeId</c> you can feed
/// into the rest of CDP (Accessibility, Overlay, etc.).
/// </summary>
/// <remarks>
/// Most methods need <see cref="EnableAsync"/> called first — CDP
/// emits <c>DOM.getDocument</c> requirements lazily otherwise. Call it
/// once after navigation.
/// </remarks>
public sealed class DomClient : CdpDomainClient
{
    internal DomClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "DOM";

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        // DOM domain emits document/attribute/childNode events we
        // don't surface as typed events at this stage — host can
        // subscribe to CefBrowser.DevToolsMessage directly if needed.
    }

    /// <summary>
    /// <c>DOM.enable</c>. Required before most other DOM methods will
    /// return stable node ids across navigations.
    /// </summary>
    public Task EnableAsync()
        => Browser.ExecuteDevToolsMethodAsync("DOM.enable", null);

    /// <summary>
    /// Reverse hit-test: which node is at viewport CSS-pixel
    /// coordinates (<paramref name="x"/>, <paramref name="y"/>)?
    /// Honours <c>pointer-events: none</c>, transforms, and shadow DOM
    /// — what's actually on top at that pixel, as Chrome itself would
    /// dispatch a click to.
    /// </summary>
    /// <param name="includeUserAgentShadowDOM">Pierce into UA shadow DOM (video controls, &lt;input type=date&gt; popups, etc.). Off by default.</param>
    /// <returns>The hit node's stable backendNodeId, or null if nothing was hit.</returns>
    public async Task<int?> GetNodeForLocationAsync(int x, int y, bool includeUserAgentShadowDOM = false)
    {
        var json = $"{{\"x\":{x},\"y\":{y},\"includeUserAgentShadowDOM\":{(includeUserAgentShadowDOM ? "true" : "false")}}}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("DOM.getNodeForLocation", json);
        var result = CdpJson.ParseResult(reply);
        return result.TryGetProperty("backendNodeId", out var b) ? b.GetInt32() : null;
    }

    /// <summary>
    /// Get the layout-box geometry of a node — content/padding/border/
    /// margin rectangles. Pair with
    /// <see cref="PageClient.CaptureScreenshotAsync"/>'s clip to
    /// screenshot just one element.
    /// </summary>
    /// <param name="backendNodeId">Stable id from <see cref="GetNodeForLocationAsync"/> or the accessibility tree.</param>
    public async Task<BoxModel?> GetBoxModelAsync(int backendNodeId)
    {
        var json = $"{{\"backendNodeId\":{backendNodeId}}}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("DOM.getBoxModel", json);
        var result = CdpJson.ParseResult(reply);
        if (!result.TryGetProperty("model", out var m)) return null;
        return new BoxModel(
            ReadQuad(m, "content"),
            ReadQuad(m, "padding"),
            ReadQuad(m, "border"),
            ReadQuad(m, "margin"),
            m.GetProperty("width").GetInt32(),
            m.GetProperty("height").GetInt32());
    }

    /// <summary>
    /// Get *all* content quads for a node — multi-rect cases like an
    /// inline link that wraps across two lines return more than one
    /// quad. <see cref="GetBoxModelAsync"/> only gives the union bbox;
    /// this is what you want to highlight every visible piece of the
    /// element.
    /// </summary>
    public async Task<IReadOnlyList<Quad>> GetContentQuadsAsync(int backendNodeId)
    {
        var json = $"{{\"backendNodeId\":{backendNodeId}}}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("DOM.getContentQuads", json);
        var result = CdpJson.ParseResult(reply);
        var quads = new List<Quad>();
        if (result.TryGetProperty("quads", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
                quads.Add(ToQuad(q));
        }
        return quads;
    }

    /// <summary>
    /// <c>DOM.describeNode</c> — node metadata (nodeName, attributes,
    /// nodeType, etc.) for a stable <paramref name="backendNodeId"/>.
    /// Returned as the raw CDP <c>node</c> element so callers can
    /// dig out exactly what they need.
    /// </summary>
    public async Task<JsonElement> DescribeNodeAsync(int backendNodeId, int depth = 1, bool pierce = false)
    {
        var json = $"{{\"backendNodeId\":{backendNodeId},\"depth\":{depth},\"pierce\":{(pierce ? "true" : "false")}}}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("DOM.describeNode", json);
        return CdpJson.ParseResult(reply).GetProperty("node");
    }

    /// <summary>
    /// Scroll the node into the viewport — gentler than driving the
    /// scroll programmatically because Chromium picks the minimal
    /// scroll needed and respects scroll containers.
    /// </summary>
    public Task ScrollIntoViewAsync(int backendNodeId)
        => Browser.ExecuteDevToolsMethodAsync(
            "DOM.scrollIntoViewIfNeeded",
            $"{{\"backendNodeId\":{backendNodeId}}}");

    /// <summary>
    /// Move keyboard focus to the node — precondition for
    /// <see cref="InputClient.InsertTextAsync"/> to land in the right
    /// field.
    /// </summary>
    public Task FocusAsync(int backendNodeId)
        => Browser.ExecuteDevToolsMethodAsync(
            "DOM.focus",
            $"{{\"backendNodeId\":{backendNodeId}}}");

    /// <summary>
    /// CSS-selector descendant lookup. <paramref name="rootNodeId"/>
    /// is a CDP <c>nodeId</c> (NOT a backendNodeId — call
    /// <see cref="GetDocumentRootAsync"/> first to get the document's
    /// root nodeId). Returns the matched node's nodeId, or 0 if no
    /// match.
    /// </summary>
    public async Task<int> QuerySelectorAsync(int rootNodeId, string selector)
    {
        var json = $"{{\"nodeId\":{rootNodeId},\"selector\":{JsonSerializer.Serialize(selector)}}}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("DOM.querySelector", json);
        return CdpJson.ParseResult(reply).GetProperty("nodeId").GetInt32();
    }

    /// <summary>
    /// Fetch the document's root node, returning its CDP
    /// <c>nodeId</c>. Use this once per page as the anchor for
    /// <see cref="QuerySelectorAsync"/>.
    /// </summary>
    public async Task<int> GetDocumentRootAsync(int depth = 1, bool pierce = false)
    {
        var json = $"{{\"depth\":{depth},\"pierce\":{(pierce ? "true" : "false")}}}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("DOM.getDocument", json);
        return CdpJson.ParseResult(reply).GetProperty("root").GetProperty("nodeId").GetInt32();
    }

    /// <summary>
    /// Resolve a CDP <c>nodeId</c> (from <see cref="QuerySelectorAsync"/>
    /// or <see cref="GetDocumentRootAsync"/>) to a stable backend node
    /// id. backendNodeIds survive navigations within a page; nodeIds
    /// don't.
    /// </summary>
    public async Task<int> ResolveToBackendNodeIdAsync(int nodeId)
    {
        var reply = await Browser.ExecuteDevToolsMethodAsync(
            "DOM.describeNode", $"{{\"nodeId\":{nodeId}}}");
        return CdpJson.ParseResult(reply).GetProperty("node").GetProperty("backendNodeId").GetInt32();
    }

    private static Quad ReadQuad(JsonElement model, string name)
        => ToQuad(model.GetProperty(name));

    private static Quad ToQuad(JsonElement arr)
    {
        // CDP quads are 8-element arrays [x1,y1, x2,y2, x3,y3, x4,y4]
        // ordered top-left, top-right, bottom-right, bottom-left.
        return new Quad(
            arr[0].GetDouble(), arr[1].GetDouble(),
            arr[2].GetDouble(), arr[3].GetDouble(),
            arr[4].GetDouble(), arr[5].GetDouble(),
            arr[6].GetDouble(), arr[7].GetDouble());
    }
}

/// <summary>
/// One CDP layout quad — 4 corners (TL, TR, BR, BL) in CSS pixels.
/// </summary>
public readonly record struct Quad(
    double TopLeftX, double TopLeftY,
    double TopRightX, double TopRightY,
    double BottomRightX, double BottomRightY,
    double BottomLeftX, double BottomLeftY)
{
    /// <summary>The quad's axis-aligned bounding box, useful for screenshot clipping.</summary>
    public ScreenshotClip ToBoundingClip(double scale = 1.0)
    {
        double minX = Math.Min(Math.Min(TopLeftX, TopRightX), Math.Min(BottomLeftX, BottomRightX));
        double minY = Math.Min(Math.Min(TopLeftY, TopRightY), Math.Min(BottomLeftY, BottomRightY));
        double maxX = Math.Max(Math.Max(TopLeftX, TopRightX), Math.Max(BottomLeftX, BottomRightX));
        double maxY = Math.Max(Math.Max(TopLeftY, TopRightY), Math.Max(BottomLeftY, BottomRightY));
        return new ScreenshotClip(minX, minY, maxX - minX, maxY - minY, scale);
    }
}

/// <summary>
/// CDP's <c>DOM.BoxModel</c> — the four standard CSS boxes plus
/// element dimensions.
/// </summary>
public sealed record BoxModel(Quad Content, Quad Padding, Quad Border, Quad Margin, int Width, int Height);
