using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>DOMSnapshot</c> domain — single-call
/// flattened DOM-plus-layout-plus-style capture across all frames,
/// returning every node's text, bounding box, computed styles, and
/// click-ability. Reached via <see cref="CefBrowser.DomSnapshot"/>.
/// </summary>
/// <remarks>
/// Beats <c>DOM.getDocument</c> + per-node <c>getBoxModel</c> +
/// <c>getComputedStyleForNode</c> round-tripping by 10–100x for
/// whole-page analysis. Single payload contains everything an AI
/// needs to reason about layout, text, and visibility.
/// </remarks>
public sealed class DomSnapshotClient : CdpDomainClient
{
    internal DomSnapshotClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "DOMSnapshot";

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json) { /* no events */ }

    /// <summary>
    /// Raw CDP snapshot payload — full fidelity, column-oriented.
    /// Useful when you want fields the flattened projection doesn't
    /// surface (paint order, stacking contexts, scroll rects, …).
    /// </summary>
    /// <param name="computedStyles">Names of computed styles to capture (e.g. ["cursor","display","visibility"]). Empty = none.</param>
    /// <param name="includePaintOrder">Include each node's paint-order index (= effective z-order).</param>
    /// <param name="includeDOMRects">Include offset/client/scroll rects per node.</param>
    public async Task<JsonElement> CaptureRawAsync(
        string[]? computedStyles = null,
        bool includePaintOrder = true,
        bool includeDOMRects = true)
    {
        var sb = new StringBuilder("{\"computedStyles\":[");
        if (computedStyles is not null)
        {
            for (int i = 0; i < computedStyles.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonSerializer.Serialize(computedStyles[i]));
            }
        }
        sb.Append("],\"includePaintOrder\":").Append(includePaintOrder ? "true" : "false")
          .Append(",\"includeDOMRects\":").Append(includeDOMRects ? "true" : "false")
          .Append('}');
        var reply = await Browser.ExecuteDevToolsMethodAsync("DOMSnapshot.captureSnapshot", sb.ToString());
        return CdpJson.ParseResult(reply);
    }

    /// <summary>
    /// Flattened, AI-friendly view of every node on the page across
    /// every frame: name, attributes, text, computed styles, bounding
    /// box, parent linkage, click-ability. The column-oriented CDP
    /// payload is zipped into row objects so you can iterate naturally.
    /// </summary>
    /// <param name="computedStyles">Computed-style names to fetch per node (e.g. ["cursor","color","visibility"]). Names you don't request are absent from each node's <see cref="DomSnapshotNode.ComputedStyles"/>.</param>
    public async Task<DomSnapshot> CaptureAsync(string[]? computedStyles = null)
    {
        var result = await CaptureRawAsync(computedStyles, includePaintOrder: true, includeDOMRects: true);
        return DomSnapshot.Parse(result, computedStyles ?? Array.Empty<string>());
    }
}

/// <summary>
/// Flattened DOM snapshot — one or more <see cref="DomDocumentSnapshot"/>
/// (one per frame).
/// </summary>
public sealed class DomSnapshot
{
    /// <summary>One entry per frame, in iteration order.</summary>
    public IReadOnlyList<DomDocumentSnapshot> Documents { get; }

    private DomSnapshot(IReadOnlyList<DomDocumentSnapshot> documents) { Documents = documents; }

    internal static DomSnapshot Parse(JsonElement result, string[] computedStyleNames)
    {
        // Shared string table. Every other reference is an index into this.
        var strings = ParseStringTable(result.GetProperty("strings"));
        var docs = new List<DomDocumentSnapshot>();
        foreach (var docElem in result.GetProperty("documents").EnumerateArray())
            docs.Add(DomDocumentSnapshot.Parse(docElem, strings, computedStyleNames));
        return new DomSnapshot(docs);
    }

    private static string[] ParseStringTable(JsonElement arr)
    {
        var strings = new string[arr.GetArrayLength()];
        int i = 0;
        foreach (var s in arr.EnumerateArray())
            strings[i++] = s.GetString() ?? "";
        return strings;
    }
}

/// <summary>
/// All nodes from one frame, flattened and zipped with their layout +
/// computed-style data.
/// </summary>
public sealed class DomDocumentSnapshot
{
    /// <summary>Document URL.</summary>
    public string DocumentUrl { get; }
    /// <summary>Document title (&lt;title&gt; text).</summary>
    public string Title { get; }
    /// <summary>Base URL for relative-link resolution.</summary>
    public string BaseUrl { get; }
    /// <summary>Every node in document order — text nodes included.</summary>
    public IReadOnlyList<DomSnapshotNode> Nodes { get; }

    private DomDocumentSnapshot(string documentUrl, string title, string baseUrl, IReadOnlyList<DomSnapshotNode> nodes)
    {
        DocumentUrl = documentUrl; Title = title; BaseUrl = baseUrl; Nodes = nodes;
    }

    internal static DomDocumentSnapshot Parse(JsonElement doc, string[] strings, string[] computedStyleNames)
    {
        string url   = Str(strings, doc.GetProperty("documentURL"));
        string title = Str(strings, doc.GetProperty("title"));
        string baseU = Str(strings, doc.GetProperty("baseURL"));

        var nodesObj = doc.GetProperty("nodes");
        var parentIndex   = IntArr(nodesObj.GetProperty("parentIndex"));
        var nodeType      = IntArr(nodesObj.GetProperty("nodeType"));
        var nodeName      = IntArr(nodesObj.GetProperty("nodeName"));
        var nodeValue     = IntArr(nodesObj.GetProperty("nodeValue"));
        var backendNodeId = IntArr(nodesObj.GetProperty("backendNodeId"));
        var attributes    = OptionalArrayOfArrays(nodesObj, "attributes");
        var isClickable   = OptionalRareIndexSet(nodesObj, "isClickable");

        // Layout tree: separate parallel arrays keyed by nodeIndex.
        var layoutObj   = doc.GetProperty("layout");
        var layoutNodeIndex = IntArr(layoutObj.GetProperty("nodeIndex"));
        var layoutText      = OptionalIntArr(layoutObj, "text");
        var layoutBounds    = OptionalBoundsArr(layoutObj, "bounds");
        var layoutStyles    = OptionalArrayOfIntArrays(layoutObj, "styles");
        var paintOrders     = OptionalIntArr(layoutObj, "paintOrders");

        // Build node → layout-index reverse map for fast joining.
        var layoutByNode = new Dictionary<int, int>(layoutNodeIndex.Length);
        for (int i = 0; i < layoutNodeIndex.Length; i++)
            layoutByNode[layoutNodeIndex[i]] = i;

        int count = parentIndex.Length;
        var nodes = new List<DomSnapshotNode>(count);
        for (int i = 0; i < count; i++)
        {
            // Attributes for this node — array of [name_idx, value_idx, …] pairs.
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (attributes is not null && i < attributes.Length && attributes[i].Length >= 2)
            {
                var pairs = attributes[i];
                for (int k = 0; k + 1 < pairs.Length; k += 2)
                {
                    if (pairs[k] < 0 || pairs[k + 1] < 0) continue;
                    string name = strings[pairs[k]];
                    string val  = strings[pairs[k + 1]];
                    attrs[name] = val;
                }
            }

            // Layout-side fields if this node has a layout box.
            string text = "";
            ScreenshotClip? bounds = null;
            int? paintOrder = null;
            var styles = new Dictionary<string, string>(StringComparer.Ordinal);
            if (layoutByNode.TryGetValue(i, out int li))
            {
                if (layoutText is not null && li < layoutText.Length && layoutText[li] >= 0)
                    text = strings[layoutText[li]];
                if (layoutBounds is not null && li < layoutBounds.Length)
                {
                    var b = layoutBounds[li];
                    bounds = new ScreenshotClip(b[0], b[1], b[2], b[3], 1.0);
                }
                if (paintOrders is not null && li < paintOrders.Length)
                    paintOrder = paintOrders[li];
                if (layoutStyles is not null && li < layoutStyles.Length)
                {
                    var styleIndices = layoutStyles[li];
                    for (int k = 0; k < styleIndices.Length && k < computedStyleNames.Length; k++)
                    {
                        if (styleIndices[k] < 0) continue;
                        styles[computedStyleNames[k]] = strings[styleIndices[k]];
                    }
                }
            }

            nodes.Add(new DomSnapshotNode(
                index: i,
                parentIndex:  i < parentIndex.Length    && parentIndex[i] >= 0    ? parentIndex[i]    : null,
                nodeType:     i < nodeType.Length      ? nodeType[i]              : 0,
                nodeName:     i < nodeName.Length      ? Str(strings, nodeName[i])  : "",
                nodeValue:    i < nodeValue.Length     ? Str(strings, nodeValue[i]) : "",
                backendNodeId: i < backendNodeId.Length ? backendNodeId[i] : 0,
                attributes:   attrs,
                isClickable:  isClickable.Contains(i),
                text:         text,
                boundingBox:  bounds,
                paintOrder:   paintOrder,
                computedStyles: styles));
        }
        return new DomDocumentSnapshot(url, title, baseU, nodes);
    }

    private static string Str(string[] table, JsonElement idxElem)
        => Str(table, idxElem.GetInt32());

    private static string Str(string[] table, int idx)
        => idx >= 0 && idx < table.Length ? table[idx] : "";

    private static int[] IntArr(JsonElement arr)
    {
        var r = new int[arr.GetArrayLength()];
        int i = 0;
        foreach (var v in arr.EnumerateArray()) r[i++] = v.GetInt32();
        return r;
    }

    private static int[]? OptionalIntArr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var arr) ? IntArr(arr) : null;

    private static int[][]? OptionalArrayOfIntArrays(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var outer)) return null;
        var r = new int[outer.GetArrayLength()][];
        int i = 0;
        foreach (var inner in outer.EnumerateArray()) r[i++] = IntArr(inner);
        return r;
    }

    private static int[][]? OptionalArrayOfArrays(JsonElement obj, string name)
        => OptionalArrayOfIntArrays(obj, name);

    private static double[][]? OptionalBoundsArr(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var outer)) return null;
        var r = new double[outer.GetArrayLength()][];
        int i = 0;
        foreach (var inner in outer.EnumerateArray())
        {
            var b = new double[4];
            int k = 0;
            foreach (var v in inner.EnumerateArray())
            {
                if (k >= 4) break;
                b[k++] = v.GetDouble();
            }
            r[i++] = b;
        }
        return r;
    }

    /// <summary>
    /// CDP "rare" fields are a struct of <c>{index: int[], value: T[]}</c>
    /// — only nodes that have the field appear, addressed by index.
    /// For boolean rare fields like <c>isClickable</c>, the <c>value</c>
    /// is implicitly true for every listed index.
    /// </summary>
    private static HashSet<int> OptionalRareIndexSet(JsonElement obj, string name)
    {
        var set = new HashSet<int>();
        if (!obj.TryGetProperty(name, out var rare)) return set;
        if (!rare.TryGetProperty("index", out var idx) || idx.ValueKind != JsonValueKind.Array) return set;
        foreach (var v in idx.EnumerateArray()) set.Add(v.GetInt32());
        return set;
    }
}

/// <summary>
/// One node in a <see cref="DomSnapshot"/>, with DOM-side and
/// layout-side fields merged. Text nodes (<see cref="NodeType"/> = 3)
/// carry their string in <see cref="NodeValue"/>; element nodes carry
/// their rendered text in <see cref="Text"/>.
/// </summary>
public sealed class DomSnapshotNode
{
    /// <summary>Index of this node in <see cref="DomDocumentSnapshot.Nodes"/>.</summary>
    public int Index { get; }
    /// <summary>Parent node's index; null for the document root.</summary>
    public int? ParentIndex { get; }
    /// <summary>DOM node type — 1 = Element, 3 = Text, 8 = Comment, 9 = Document, …</summary>
    public int NodeType { get; }
    /// <summary>Tag name (uppercase for HTML) for elements; "#text" / "#comment" for non-elements.</summary>
    public string NodeName { get; }
    /// <summary>Raw text content for text/comment nodes; empty for elements.</summary>
    public string NodeValue { get; }
    /// <summary>Stable DOM backend node id — cross-CDP-domain handle (AX, Overlay, etc.).</summary>
    public int BackendNodeId { get; }
    /// <summary>Element attributes (name → value); empty for non-elements.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }
    /// <summary>True if Blink marks this node as click-handled — the cheap "is this clickable?" check.</summary>
    public bool IsClickable { get; }
    /// <summary>Layout-rendered text content (the visible text if any). Different from <see cref="NodeValue"/> in that this is post-CSS, post-line-break.</summary>
    public string Text { get; }
    /// <summary>Bounding box in CSS pixels (page-relative). Null if the node isn't laid out (script tags, &lt;head&gt; contents, display:none).</summary>
    public ScreenshotClip? BoundingBox { get; }
    /// <summary>Paint order — higher = drawn later = visually on top.</summary>
    public int? PaintOrder { get; }
    /// <summary>Computed-style values for the styles you requested in <see cref="DomSnapshotClient.CaptureAsync"/>.</summary>
    public IReadOnlyDictionary<string, string> ComputedStyles { get; }

    internal DomSnapshotNode(
        int index, int? parentIndex, int nodeType, string nodeName, string nodeValue,
        int backendNodeId, IReadOnlyDictionary<string, string> attributes,
        bool isClickable, string text, ScreenshotClip? boundingBox, int? paintOrder,
        IReadOnlyDictionary<string, string> computedStyles)
    {
        Index = index; ParentIndex = parentIndex; NodeType = nodeType;
        NodeName = nodeName; NodeValue = nodeValue;
        BackendNodeId = backendNodeId; Attributes = attributes;
        IsClickable = isClickable; Text = text; BoundingBox = boundingBox; PaintOrder = paintOrder;
        ComputedStyles = computedStyles;
    }
}
