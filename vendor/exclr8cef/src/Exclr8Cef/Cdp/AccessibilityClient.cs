using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>Accessibility</c> domain — Chromium's
/// internally-maintained accessibility tree exposed as a typed
/// hierarchical page model. Reached via
/// <see cref="CefBrowser.Accessibility"/>.
/// </summary>
/// <remarks>
/// <para>
/// THE highest-leverage AI-targeting signal: a role-labeled,
/// hierarchical, semantic view of the page ("button named 'Sign in'",
/// "textbox labeled 'Email'", "list with 12 items"). Far cheaper than
/// screenshot+OCR or <c>outerHTML</c> scraping, and far more robust
/// to visual / structural variance across sites.
/// </para>
/// <para>
/// Call <see cref="EnableAsync"/> once after the page is loaded. The
/// AX tree updates continuously; subscribe to <see cref="NodesUpdated"/>
/// for live diffs or just re-call <see cref="GetFullTreeAsync"/> when
/// you want a snapshot.
/// </para>
/// </remarks>
public sealed class AccessibilityClient : CdpDomainClient
{
    internal AccessibilityClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "Accessibility";

    /// <summary>
    /// Fires when CEF has new or updated AX nodes — incremental diffs
    /// from Blink as the page mutates. Use this to keep a cached AX
    /// tree in sync without re-walking the whole thing.
    /// </summary>
    public event EventHandler<AxNodesUpdatedEventArgs>? NodesUpdated
    {
        add { EnsureEventSubscription(); _nodesUpdated += value; }
        remove { _nodesUpdated -= value; }
    }
    private EventHandler<AxNodesUpdatedEventArgs>? _nodesUpdated;

    /// <summary>
    /// Fires when an iframe's AX tree finishes loading.
    /// </summary>
    public event EventHandler<AxLoadCompleteEventArgs>? LoadComplete
    {
        add { EnsureEventSubscription(); _loadComplete += value; }
        remove { _loadComplete -= value; }
    }
    private EventHandler<AxLoadCompleteEventArgs>? _loadComplete;

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        switch (method)
        {
            case "Accessibility.nodesUpdated":
                if (_nodesUpdated is null) return;
                var p = CdpJson.ParseEventParams(json);
                _nodesUpdated.Invoke(this, new AxNodesUpdatedEventArgs(ParseNodes(p.GetProperty("nodes"))));
                break;
            case "Accessibility.loadComplete":
                if (_loadComplete is null) return;
                var lp = CdpJson.ParseEventParams(json);
                _loadComplete.Invoke(this, new AxLoadCompleteEventArgs(ParseNode(lp.GetProperty("root"))));
                break;
        }
    }

    /// <summary>
    /// Enable the Accessibility domain. CDP doesn't produce AX events
    /// or accept queries until this is called once. Costs roughly one
    /// AXMode level on the page; safe to leave on.
    /// </summary>
    public Task EnableAsync()
        => Browser.ExecuteDevToolsMethodAsync("Accessibility.enable", null);

    /// <summary>Disable the Accessibility domain.</summary>
    public Task DisableAsync()
        => Browser.ExecuteDevToolsMethodAsync("Accessibility.disable", null);

    /// <summary>
    /// Full-page AX tree as a flat list of nodes. Walk parent/child
    /// ids to reconstruct the tree, or just filter by role/name to
    /// find what you want.
    /// </summary>
    /// <param name="maxDepth">Truncate the tree at this depth (null = unbounded).</param>
    /// <param name="frameId">Limit to a specific frame; null = main frame.</param>
    public async Task<IReadOnlyList<AxNode>> GetFullTreeAsync(int? maxDepth = null, string? frameId = null)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        if (maxDepth is int d) { sb.Append("\"depth\":").Append(d); first = false; }
        if (frameId is not null)
        {
            if (!first) sb.Append(',');
            sb.Append("\"frameId\":").Append(JsonSerializer.Serialize(frameId));
        }
        sb.Append('}');
        var reply = await Browser.ExecuteDevToolsMethodAsync("Accessibility.getFullAXTree", sb.ToString());
        return ParseNodes(CdpJson.ParseResult(reply).GetProperty("nodes"));
    }

    /// <summary>
    /// Filtered AX-tree query — "find every node with role=button" or
    /// "find every node with name='Sign in'", optionally rooted at a
    /// specific node. The fast path for "where's the X on this page?"
    /// without walking the full tree.
    /// </summary>
    /// <param name="accessibleName">Optional name to match (exact).</param>
    /// <param name="role">Optional ARIA role to match (exact).</param>
    /// <param name="rootBackendNodeId">Optional DOM-side scope root.</param>
    public async Task<IReadOnlyList<AxNode>> QueryAsync(
        string? accessibleName = null, string? role = null, int? rootBackendNodeId = null)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        if (rootBackendNodeId is int id)
        {
            sb.Append("\"backendNodeId\":").Append(id);
            first = false;
        }
        if (accessibleName is not null)
        {
            if (!first) sb.Append(',');
            sb.Append("\"accessibleName\":").Append(JsonSerializer.Serialize(accessibleName));
            first = false;
        }
        if (role is not null)
        {
            if (!first) sb.Append(',');
            sb.Append("\"role\":").Append(JsonSerializer.Serialize(role));
        }
        sb.Append('}');
        var reply = await Browser.ExecuteDevToolsMethodAsync("Accessibility.queryAXTree", sb.ToString());
        return ParseNodes(CdpJson.ParseResult(reply).GetProperty("nodes"));
    }

    /// <summary>
    /// Partial AX tree rooted at a specific DOM node — useful when
    /// you've narrowed down via <see cref="DomClient.GetNodeForLocationAsync"/>
    /// and want the AX subtree under that point.
    /// </summary>
    public async Task<IReadOnlyList<AxNode>> GetPartialTreeAsync(int backendNodeId, bool fetchRelatives = true)
    {
        var json = $"{{\"backendNodeId\":{backendNodeId},\"fetchRelatives\":{(fetchRelatives ? "true" : "false")}}}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("Accessibility.getPartialAXTree", json);
        return ParseNodes(CdpJson.ParseResult(reply).GetProperty("nodes"));
    }

    // ---- Parsing -----------------------------------------------------

    private static IReadOnlyList<AxNode> ParseNodes(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<AxNode>();
        var nodes = new List<AxNode>(arr.GetArrayLength());
        foreach (var n in arr.EnumerateArray()) nodes.Add(ParseNode(n));
        return nodes;
    }

    private static AxNode ParseNode(JsonElement n)
    {
        string id          = n.TryGetProperty("nodeId", out var ni) ? ni.GetString() ?? "" : "";
        bool ignored       = n.TryGetProperty("ignored", out var ig) && ig.GetBoolean();
        string role        = ReadValueValue(n, "role");
        string name        = ReadValueValue(n, "name");
        string description = ReadValueValue(n, "description");
        string value       = ReadValueValue(n, "value");
        int? backendDom    = n.TryGetProperty("backendDOMNodeId", out var b) ? b.GetInt32() : null;
        string? parentId   = n.TryGetProperty("parentId", out var pi) ? pi.GetString() : null;

        var childIds = new List<string>();
        if (n.TryGetProperty("childIds", out var ci) && ci.ValueKind == JsonValueKind.Array)
            foreach (var c in ci.EnumerateArray())
                if (c.GetString() is string s) childIds.Add(s);

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (n.TryGetProperty("properties", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var prop in ps.EnumerateArray())
            {
                if (!prop.TryGetProperty("name", out var pn)) continue;
                var key = pn.GetString();
                if (key is null) continue;
                properties[key] = ReadValue(prop);
            }
        }

        return new AxNode(id, backendDom, ignored, role, name, description, value, parentId, childIds, properties);
    }

    /// <summary>
    /// Read the <c>.value</c> from a <c>{name|role|description|…}</c>
    /// sub-object. CDP wraps each in a <c>{type, value, sources?}</c>
    /// envelope. Returns empty string if absent.
    /// </summary>
    private static string ReadValueValue(JsonElement node, string field)
        => node.TryGetProperty(field, out var sub) ? ReadValue(sub) : "";

    private static string ReadValue(JsonElement sub)
    {
        if (!sub.TryGetProperty("value", out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String                    => v.GetString() ?? "",
            JsonValueKind.Number                    => v.GetRawText(),
            JsonValueKind.True                      => "true",
            JsonValueKind.False                     => "false",
            JsonValueKind.Array or JsonValueKind.Object => v.GetRawText(),
            _                                       => ""
        };
    }
}

/// <summary>
/// One node in Chromium's accessibility tree. Fields collapse CDP's
/// verbose <c>{type, value, sources}</c> envelope to the bare <c>value</c>
/// — the AI rarely cares about *how* the name was computed, just what
/// it is.
/// </summary>
public sealed class AxNode
{
    /// <summary>AX-tree node id. NOT a DOM nodeId — use <see cref="BackendDomNodeId"/> for cross-domain lookup.</summary>
    public string Id { get; }
    /// <summary>Stable DOM backend node id (cross-CDP-domain handle); null for AX-only nodes.</summary>
    public int? BackendDomNodeId { get; }
    /// <summary>True if this node is hidden from the accessibility tree (visually present but a11y-irrelevant).</summary>
    public bool Ignored { get; }
    /// <summary>ARIA role, e.g. "button", "textbox", "list", "heading", "link".</summary>
    public string Role { get; }
    /// <summary>Computed accessible name — what a screen reader would say.</summary>
    public string Name { get; }
    /// <summary>Computed accessible description (longer than name; aria-describedby et al.).</summary>
    public string Description { get; }
    /// <summary>For inputs and the like — the current value.</summary>
    public string Value { get; }
    /// <summary>Parent AX node id (null for the root).</summary>
    public string? ParentId { get; }
    /// <summary>Child AX node ids in document order.</summary>
    public IReadOnlyList<string> ChildIds { get; }
    /// <summary>Role-specific properties: "checked" (checkboxes), "expanded" (combobox), "level" (heading), "selected" (option), etc.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; }

    internal AxNode(string id, int? backendDomNodeId, bool ignored,
                    string role, string name, string description, string value,
                    string? parentId, IReadOnlyList<string> childIds,
                    IReadOnlyDictionary<string, string> properties)
    {
        Id = id; BackendDomNodeId = backendDomNodeId; Ignored = ignored;
        Role = role; Name = name; Description = description; Value = value;
        ParentId = parentId; ChildIds = childIds; Properties = properties;
    }

    /// <summary>Compact debug representation: <c>role 'name' (#id)</c>.</summary>
    public override string ToString()
        => string.IsNullOrEmpty(Name) ? $"{Role} (#{Id})" : $"{Role} '{Name}' (#{Id})";
}

/// <summary>Args for <see cref="AccessibilityClient.NodesUpdated"/>.</summary>
public sealed class AxNodesUpdatedEventArgs : EventArgs
{
    /// <summary>The set of nodes that were added or modified in this update.</summary>
    public IReadOnlyList<AxNode> Nodes { get; }
    internal AxNodesUpdatedEventArgs(IReadOnlyList<AxNode> nodes) { Nodes = nodes; }
}

/// <summary>Args for <see cref="AccessibilityClient.LoadComplete"/>.</summary>
public sealed class AxLoadCompleteEventArgs : EventArgs
{
    /// <summary>The root node of the (now-complete) AX tree for the frame.</summary>
    public AxNode Root { get; }
    internal AxLoadCompleteEventArgs(AxNode root) { Root = root; }
}
