using System.Text;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal static class BrowserAgentToolParser
{
    private const int MaximumUrlLength = 2_048;
    private const int MaximumFillTextBytes = 2 * 1_024;

    public static BrowserAgentIntentResult Parse(AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid("Tool arguments must be an object.");
        }

        if (!TryReadUniqueProperties(proposal.Arguments, out var properties))
        {
            return Invalid("Tool arguments cannot contain duplicate fields.");
        }

        return ParseProperties(proposal.ToolName, properties, panelId: null);
    }

    /// <summary>
    /// Parses a proposal for one exact browser panel. The provider cannot
    /// repeat or replace the host-owned panel identity in its arguments.
    /// </summary>
    public static BrowserAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid("Tool arguments must be an object.");
        }

        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadUniqueProperties(proposal.Arguments, out var properties))
        {
            return Invalid("Tool arguments cannot contain duplicate fields.");
        }

        if (properties.ContainsKey("panel_id"))
        {
            return Invalid(
                "An exact browser tool does not accept a panel ID.");
        }

        return BrowserAgentToolSet.Supports(panel, proposal.ToolName)
            ? ParseProperties(
                proposal.ToolName,
                properties,
                panel.PanelId)
            : UnavailableTool();
    }

    /// <summary>
    /// Parses a proposal against a freshly resolved broad browser scope. Even
    /// a scope with one eligible browser requires its explicit panel ID so the
    /// schema shape cannot silently change with live capability availability.
    /// </summary>
    public static BrowserAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid("Tool arguments must be an object.");
        }

        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadUniqueProperties(proposal.Arguments, out var properties))
        {
            return Invalid("Tool arguments cannot contain duplicate fields.");
        }

        var activeBrowsers = BrowserAgentToolSet.ActiveBrowsers(panels);
        if (activeBrowsers.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || panelIdElement.GetString() is not { } panelId)
        {
            return Invalid(
                "A multi-browser tool requires one exact panel_id.");
        }

        var selected = activeBrowsers.FirstOrDefault(
            panel => string.Equals(
                panel.PanelId.Value,
                panelId,
                StringComparison.Ordinal));
        if (selected is null
            || !BrowserAgentToolSet.Supports(
                selected,
                proposal.ToolName))
        {
            return Invalid(
                "The selected panel_id is not available for this browser tool.");
        }

        properties.Remove("panel_id");
        return ParseProperties(
            proposal.ToolName,
            properties,
            selected.PanelId);
    }

    private static BrowserAgentIntentResult ParseProperties(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId) =>
        toolName switch
        {
            BuiltInAgentTools.BrowserReadState =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.ReadState(),
                    panelId),
            BuiltInAgentTools.BrowserSnapshot =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Snapshot(),
                    panelId),
            BuiltInAgentTools.BrowserClick =>
                ParseClick(properties, panelId),
            BuiltInAgentTools.BrowserFill =>
                ParseFill(properties, panelId),
            BuiltInAgentTools.BrowserCheck =>
                ParseCheck(properties, panelId),
            BuiltInAgentTools.BrowserNavigate =>
                ParseNavigate(properties, panelId),
            BuiltInAgentTools.BrowserBack =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Back(),
                    panelId),
            BuiltInAgentTools.BrowserForward =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Forward(),
                    panelId),
            BuiltInAgentTools.BrowserReload =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Reload(),
                    panelId),
            BuiltInAgentTools.BrowserStop =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Stop(),
                    panelId),
            _ => UnknownTool(),
        };

    private static BrowserAgentIntentResult ParseEmpty(
        IReadOnlyDictionary<string, JsonElement> properties,
        BrowserAgentIntent intent,
        PanelInstanceId? panelId) =>
        properties.Count == 0
            ? new BrowserAgentIntentResult.Parsed(intent, panelId)
            : Invalid("This tool does not accept arguments.");

    private static BrowserAgentIntentResult ParseNavigate(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || urlElement.GetString() is not { } url
            || url.Length is < 1 or > MaximumUrlLength
            || !string.Equals(url, url.Trim(), StringComparison.Ordinal)
            || !BrowserAddress.TryParse(url, out var address))
        {
            return Invalid(
                "Browser navigation requires one absolute HTTP(S) URL "
                + "or about:blank of at most 2048 characters.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Navigate(address),
            panelId);
    }

    private static BrowserAgentIntentResult ParseClick(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !TryReadElementBinding(
                properties,
                out var reference,
                out var documentRevision))
        {
            return Invalid(
                "Browser click requires one URL-safe reference of at most "
                + "128 characters and one non-negative integer document_revision.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Click(
                new BrowserElementReferenceId(reference),
                documentRevision),
            panelId);
    }

    private static BrowserAgentIntentResult ParseFill(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 3
            || !TryReadElementBinding(
                properties,
                out var reference,
                out var documentRevision)
            || !properties.TryGetValue("text", out var textElement)
            || !TryGetString(textElement, out var text)
            || !IsAllowedFillText(text))
        {
            return Invalid(
                "Browser fill requires one URL-safe reference of at most "
                + "128 characters, one non-negative integer document_revision, "
                + "and well-formed text of at most 2048 UTF-8 bytes. Only tabs "
                + "and line breaks are allowed as control characters.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Fill(
                new BrowserElementReferenceId(reference),
                documentRevision,
                text),
            panelId);
    }

    private static BrowserAgentIntentResult ParseCheck(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !TryReadElementBinding(
                properties,
                out var reference,
                out var documentRevision))
        {
            return Invalid(
                "Browser check requires one URL-safe reference of at most "
                + "128 characters and one non-negative integer document_revision.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Check(
                new BrowserElementReferenceId(reference),
                documentRevision),
            panelId);
    }

    private static bool TryReadElementBinding(
        IReadOnlyDictionary<string, JsonElement> properties,
        out string reference,
        out long documentRevision)
    {
        reference = string.Empty;
        documentRevision = 0;
        if (!properties.TryGetValue(
                "reference",
                out var referenceElement)
            || referenceElement.ValueKind != JsonValueKind.String
            || !TryGetString(referenceElement, out var referenceValue)
            || referenceValue.Length
                is < 1 or > BrowserElementReferenceId.MaximumValueBytes
            || referenceValue.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_')
            || !properties.TryGetValue(
                "document_revision",
                out var revisionElement)
            || revisionElement.ValueKind != JsonValueKind.Number
            || !revisionElement.TryGetInt64(out documentRevision)
            || documentRevision < 0)
        {
            return false;
        }

        reference = referenceValue;
        return true;
    }

    private static bool TryGetString(
        JsonElement element,
        out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

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

    private static bool IsAllowedFillText(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumFillTextBytes
            || text.Any(character =>
                char.IsControl(character)
                && character is not '\t' and not '\n' and not '\r'))
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(text[index])
                || index + 1 >= text.Length
                || !char.IsLowSurrogate(text[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static bool TryReadUniqueProperties(
        JsonElement arguments,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                properties.Clear();
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownTool(string toolName) =>
        toolName is
            BuiltInAgentTools.BrowserReadState
            or BuiltInAgentTools.BrowserSnapshot
            or BuiltInAgentTools.BrowserClick
            or BuiltInAgentTools.BrowserFill
            or BuiltInAgentTools.BrowserCheck
            or BuiltInAgentTools.BrowserNavigate
            or BuiltInAgentTools.BrowserBack
            or BuiltInAgentTools.BrowserForward
            or BuiltInAgentTools.BrowserReload
            or BuiltInAgentTools.BrowserStop;

    private static BrowserAgentIntentResult UnknownTool() =>
        new BrowserAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a tool that is not available to this run.");

    private static BrowserAgentIntentResult UnavailableTool() =>
        new BrowserAgentIntentResult.Rejected(
            "tool_not_available",
            "The browser tool is not available in the freshly resolved scope.");

    private static BrowserAgentIntentResult Invalid(string message) =>
        new BrowserAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}
