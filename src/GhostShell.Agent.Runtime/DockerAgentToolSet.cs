using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal static class DockerAgentToolSet
{
    private static readonly ToolSpec[] Specifications =
    [
        new(
            BuiltInAgentTools.DockerReadState,
            SessionCapabilities.DockerReadState,
            "Read bounded engine and resource summaries from one exact hosted Docker panel. Returns opaque resource_ref values, never daemon endpoints or raw resource IDs.",
            WriteReadState),
        new(
            BuiltInAgentTools.DockerInspect,
            SessionCapabilities.DockerInspect,
            "Inspect a bounded safe-property allowlist for one exact opaque Docker resource_ref. Environment, labels, mounts, host paths, and raw inspect JSON are excluded.",
            WriteInspect),
        new(
            BuiltInAgentTools.DockerLogs,
            SessionCapabilities.DockerReadLogs,
            "Read one bounded page of sanitized container logs using an opaque container_ref.",
            WriteLogs),
        new(
            BuiltInAgentTools.DockerFilesList,
            SessionCapabilities.DockerFilesList,
            "List bounded provider-relative files under one absolute container/image path and opaque resource_ref. Shellless containers return docker_filesystem_unavailable; exact known-path reads may still work.",
            WriteFilesList),
        new(
            BuiltInAgentTools.DockerFilesStat,
            SessionCapabilities.DockerFilesStat,
            "Read bounded metadata for one exact provider-relative Docker file path.",
            WriteFilesStat),
        new(
            BuiltInAgentTools.DockerFileRead,
            SessionCapabilities.DockerFilesRead,
            "Read bounded strict UTF-8 text from one exact provider-relative Docker file path. Binary/base64 content is never returned.",
            WriteFileRead),
    ];

    public static ImmutableArray<AgentToolDefinition> For(AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (!SupportsDockerPanel(panel))
        {
            return [];
        }

        return [.. Specifications
            .Where(specification => Supports(panel, specification.Capability))
            .Select(specification => Tool(specification, panelIds: null))];
    }

    public static ImmutableArray<AgentToolDefinition> For(
        IReadOnlyList<AgentContextPanel> panels)
    {
        var eligible = ActiveDockerPanels(panels);
        if (eligible.Length == 0)
        {
            return [];
        }

        var tools = ImmutableArray.CreateBuilder<AgentToolDefinition>();
        foreach (var specification in Specifications)
        {
            var panelIds = eligible
                .Where(panel => Supports(panel, specification.Capability))
                .Select(panel => panel.PanelId)
                .ToArray();
            if (panelIds.Length > 0)
            {
                tools.Add(Tool(specification, panelIds));
            }
        }

        return tools.ToImmutable();
    }

    public static ImmutableArray<AgentToolDefinition> ForWorkspace() =>
        [.. Specifications
            .Select(specification => AgentToolScopeSchema.WithRequiredPanelId(
                Tool(specification, panelIds: null)))];

    internal static ImmutableArray<AgentContextPanel> ActiveDockerPanels(
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        if (panels.Count is < 1 or > AgentContextRequest.MaximumAllowedPanelCount
            || panels.Select(panel => panel.PanelId).Distinct().Count() != panels.Count)
        {
            throw new ArgumentException(
                "A Docker tool scope requires a bounded unique panel collection.",
                nameof(panels));
        }

        return [.. panels.Where(SupportsDockerPanel)];
    }

    internal static bool Supports(AgentContextPanel panel, string capability) =>
        SupportsDockerPanel(panel)
        && panel.Capabilities.Contains(capability, StringComparer.Ordinal);

    internal static string? RequiredCapability(string toolName) =>
        Specifications.FirstOrDefault(specification => string.Equals(
            specification.Name,
            toolName,
            StringComparison.Ordinal))?.Capability;

    private static bool SupportsDockerPanel(AgentContextPanel panel) =>
        panel.Kind == PanelKind.Docker
        && panel.HasRegisteredGraph
        && panel.IsCurrentPanelSession
        && panel.SessionId is not null
        && panel.Lifecycle == SessionLifecycle.Active;

    private static AgentToolDefinition Tool(
        ToolSpec specification,
        IReadOnlyList<PanelInstanceId>? panelIds)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        if (panelIds is not null)
        {
            writer.WriteStartObject("panel_id");
            writer.WriteString("type", "string");
            writer.WriteStartArray("enum");
            foreach (var panelId in panelIds)
            {
                writer.WriteStringValue(panelId.Value);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        specification.WriteArguments(writer);
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        if (panelIds is not null)
        {
            writer.WriteStringValue("panel_id");
        }

        foreach (var required in RequiredArguments(specification.Name))
        {
            writer.WriteStringValue(required);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.Flush();
        return new AgentToolDefinition(
            specification.Name,
            panelIds is null
                ? specification.Description
                : $"{specification.Description} Select the exact panel with panel_id.",
            buffer.WrittenSpan.ToArray());
    }

    private static void WriteReadState(Utf8JsonWriter writer) =>
        WriteInteger(writer, "maximum_resources_per_kind", 1, 100, 50);

    private static void WriteInspect(Utf8JsonWriter writer) =>
        WriteString(writer, "resource_ref", 1, 128);

    private static void WriteLogs(Utf8JsonWriter writer)
    {
        WriteString(writer, "container_ref", 1, 128);
        WriteInteger(writer, "limit", 1, 500, 100);
        WriteString(writer, "before_timestamp", 1, 128);
        WriteString(writer, "search", 0, 512);
        WriteInteger(writer, "context_lines", 0, 50, 0);
    }

    private static void WriteFilesList(Utf8JsonWriter writer)
    {
        WriteString(writer, "resource_ref", 1, 128);
        WriteString(writer, "path", 1, 4_096);
        WriteInteger(writer, "maximum_entries", 1, 200, 100);
    }

    private static void WriteFilesStat(Utf8JsonWriter writer)
    {
        WriteString(writer, "resource_ref", 1, 128);
        WriteString(writer, "path", 1, 4_096);
    }

    private static void WriteFileRead(Utf8JsonWriter writer)
    {
        WriteString(writer, "resource_ref", 1, 128);
        WriteString(writer, "path", 1, 4_096);
        WriteInteger(writer, "maximum_bytes", 1, 16 * 1_024, 8 * 1_024);
    }

    private static IReadOnlyList<string> RequiredArguments(string toolName) =>
        toolName switch
        {
            BuiltInAgentTools.DockerInspect => ["resource_ref"],
            BuiltInAgentTools.DockerLogs => ["container_ref"],
            BuiltInAgentTools.DockerFilesList
                or BuiltInAgentTools.DockerFilesStat
                or BuiltInAgentTools.DockerFileRead => ["resource_ref", "path"],
            _ => [],
        };

    private static void WriteString(
        Utf8JsonWriter writer,
        string name,
        int minimumLength,
        int maximumLength)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteNumber("minLength", minimumLength);
        writer.WriteNumber("maxLength", maximumLength);
        writer.WriteEndObject();
    }

    private static void WriteInteger(
        Utf8JsonWriter writer,
        string name,
        int minimum,
        int maximum,
        int defaultValue)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "integer");
        writer.WriteNumber("minimum", minimum);
        writer.WriteNumber("maximum", maximum);
        writer.WriteNumber("default", defaultValue);
        writer.WriteEndObject();
    }

    private sealed record ToolSpec(
        string Name,
        string Capability,
        string Description,
        Action<Utf8JsonWriter> WriteArguments);
}
