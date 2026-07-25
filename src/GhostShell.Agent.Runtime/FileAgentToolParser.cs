using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal static class FileAgentToolParser
{
    public static FileAgentIntentResult Parse(AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        return ParseProperties(proposal.ToolName, properties, panelId: null);
    }

    /// <summary>
    /// Parses a proposal for one exact file panel. Panel and session identity
    /// remain host-owned and therefore cannot appear in provider arguments.
    /// </summary>
    public static FileAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel,
        FileSessionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        if (properties.ContainsKey("panel_id"))
        {
            return Invalid(
                "An exact file tool does not accept a panel ID.");
        }

        return FileAgentToolSet.Supports(panel, metadata, proposal.ToolName)
            ? ParseProperties(
                proposal.ToolName,
                properties,
                panel.PanelId)
            : UnavailableTool();
    }

    /// <summary>
    /// Parses a proposal against a freshly resolved broad scope. An explicit
    /// enumerated panel ID is required even when one file panel is eligible.
    /// </summary>
    public static FileAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        var activePanels = FileAgentToolSet.ActiveFilePanels(panels, metadata);
        if (activePanels.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || !TryGetString(panelIdElement, out var panelId))
        {
            return Invalid(
                "A broad file tool requires one exact panel_id.");
        }

        var selected = activePanels.FirstOrDefault(binding =>
            string.Equals(
                binding.Panel.PanelId.Value,
                panelId,
                StringComparison.Ordinal));
        if (selected is null
            || !FileAgentToolSet.Supports(
                selected.Panel,
                selected.Metadata,
                proposal.ToolName))
        {
            return Invalid(
                "The selected panel_id is not available for this file tool.");
        }

        properties.Remove("panel_id");
        return ParseProperties(
            proposal.ToolName,
            properties,
            selected.Panel.PanelId);
    }

    private static FileAgentIntentResult ParseProperties(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue(
                "path_segments",
                out var pathElement)
            || !TryReadPath(pathElement, out var path))
        {
            return Invalid(
                "File tools require only a bounded path_segments array "
                + "relative to the trusted panel root.");
        }

        return toolName switch
        {
            BuiltInAgentTools.FilesList =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.List(path),
                    panelId),
            BuiltInAgentTools.FilesStat =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.Stat(path),
                    panelId),
            BuiltInAgentTools.FilesRead when path.Length > 0 =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.Read(path),
                    panelId),
            BuiltInAgentTools.FilesRead =>
                Invalid("File read requires a non-root file path."),
            BuiltInAgentTools.FilesCreateDirectory when path.Length > 0 =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.CreateDirectory(path),
                    panelId),
            BuiltInAgentTools.FilesCreateDirectory =>
                Invalid("Directory creation requires a non-root file path."),
            BuiltInAgentTools.FilesDelete when path.Length > 0 =>
                new FileAgentIntentResult.Parsed(
                    new FileAgentIntent.Delete(path),
                    panelId),
            BuiltInAgentTools.FilesDelete =>
                Invalid("File deletion requires a non-root file path."),
            _ => UnknownTool(),
        };
    }

    private static bool TryReadPath(
        JsonElement element,
        out ImmutableArray<FilePanelPathSegment> path)
    {
        path = [];
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<FilePanelPathSegment>();
        var totalBytes = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (builder.Count == FileAgentToolSet.MaximumPathSegments
                || item.ValueKind != JsonValueKind.String
                || !TryGetString(item, out var value)
                || !IsSafePathSegment(value))
            {
                return false;
            }

            totalBytes = checked(
                totalBytes
                + Encoding.UTF8.GetByteCount(value)
                + (builder.Count == 0 ? 0 : 1));
            if (totalBytes > FileAgentToolSet.MaximumRelativePathBytes)
            {
                return false;
            }

            builder.Add(new FilePanelPathSegment(value));
        }

        path = builder.ToImmutable();
        return true;
    }

    private static bool IsSafePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(value)
                > FileAgentToolSet.MaximumPathSegmentBytes)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character)
                    || index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
        }

        return value.EnumerateRunes().All(rune =>
            Rune.GetUnicodeCategory(rune) is not (
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator));
    }

    private static bool TryReadProperties(
        AgentToolProposal proposal,
        out Dictionary<string, JsonElement> properties,
        out FileAgentIntentResult rejection)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            rejection = Invalid("Tool arguments must be an object.");
            return false;
        }

        foreach (var property in proposal.Arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                properties.Clear();
                rejection = Invalid(
                    "Tool arguments cannot contain duplicate fields.");
                return false;
            }
        }

        rejection = null!;
        return true;
    }

    private static bool TryGetString(
        JsonElement element,
        out string value)
    {
        value = string.Empty;
        try
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsKnownTool(string toolName) =>
        toolName is
            BuiltInAgentTools.FilesList
            or BuiltInAgentTools.FilesStat
            or BuiltInAgentTools.FilesRead
            or BuiltInAgentTools.FilesCreateDirectory
            or BuiltInAgentTools.FilesDelete;

    private static FileAgentIntentResult UnknownTool() =>
        new FileAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a tool that is not available to this run.");

    private static FileAgentIntentResult UnavailableTool() =>
        new FileAgentIntentResult.Rejected(
            "tool_not_available",
            "The file tool is not available in the freshly resolved scope.");

    private static FileAgentIntentResult Invalid(string message) =>
        new FileAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}
