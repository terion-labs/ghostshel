using System.Text.Json;

namespace GhostShell.Mcp;

internal sealed record McpTool(
    string Name,
    JsonElement InputSchema);
