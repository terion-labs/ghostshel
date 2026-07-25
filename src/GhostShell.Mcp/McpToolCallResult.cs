using System.Text.Json;

namespace GhostShell.Mcp;

internal sealed record McpToolCallResult(
    IReadOnlyList<McpToolCallContent> Content,
    JsonElement? StructuredContent,
    bool IsError);
