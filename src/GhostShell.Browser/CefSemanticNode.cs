namespace GhostShell.Browser;

internal sealed record CefSemanticNode(
    string Id,
    int? BackendNodeId,
    bool IsIgnored,
    string Role,
    string Name,
    string? ParentId,
    IReadOnlyList<string> ChildIds,
    IReadOnlyDictionary<string, string> Properties,
    string Value);

internal readonly record struct CefSemanticPoint(double X, double Y);
