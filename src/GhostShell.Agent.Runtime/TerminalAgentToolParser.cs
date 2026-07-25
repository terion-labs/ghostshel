using System.Text;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal static class TerminalAgentToolParser
{
    private const int MinimumGridColumns = 2;
    private const int MaximumGridDimension = 1_000;
    private const int MaximumTextBytes = 2 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(30);

    public static TerminalAgentIntentResult Parse(AgentToolProposal proposal)
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

        if (proposal.ToolName == BuiltInAgentTools.TerminalResize)
        {
            return UnavailableTool();
        }

        return ParseProperties(proposal.ToolName, properties, panelId: null);
    }

    /// <summary>
    /// Parses a proposal for one exact trusted terminal target. Exact contracts
    /// do not expose provider-selected panel identity.
    /// </summary>
    public static TerminalAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel,
        IReadOnlySet<PanelInstanceId>? resizeEligiblePanelIds = null)
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
            return Invalid("An exact terminal tool does not accept a panel ID.");
        }

        return TerminalAgentToolSet.Supports(
            panel,
            proposal.ToolName,
            resizeEligiblePanelIds)
            ? ParseProperties(
                proposal.ToolName,
                properties,
                panel.PanelId)
            : UnavailableTool();
    }

    /// <summary>
    /// Parses a proposal against a fresh resolved panel scope. Panel selection
    /// is derived only from active terminal panel IDs and is revalidated for
    /// the exact requested operation.
    /// </summary>
    public static TerminalAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels,
        IReadOnlySet<PanelInstanceId>? resizeEligiblePanelIds = null)
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

        var activeTerminals = TerminalAgentToolSet.ActiveTerminals(panels);
        if (activeTerminals.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || panelIdElement.GetString() is not { } panelId)
        {
            return Invalid(
                "A scoped terminal tool requires one exact panel_id.");
        }

        var selected = activeTerminals.FirstOrDefault(
            panel => string.Equals(
                panel.PanelId.Value,
                panelId,
                StringComparison.Ordinal));
        if (selected is null
            || !TerminalAgentToolSet.Supports(
                selected,
                proposal.ToolName,
                resizeEligiblePanelIds))
        {
            return Invalid(
                "The selected panel_id is not available for this terminal tool.");
        }

        properties.Remove("panel_id");
        return ParseProperties(
            proposal.ToolName,
            properties,
            selected.PanelId);
    }

    private static TerminalAgentIntentResult ParseProperties(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId) =>
        toolName switch
        {
            BuiltInAgentTools.TerminalReadScreen =>
                ParseEmpty(
                    properties,
                    new TerminalAgentIntent.ReadScreen(),
                    panelId),
            BuiltInAgentTools.TerminalSendText =>
                ParseSendText(properties, panelId),
            BuiltInAgentTools.TerminalPaste =>
                ParsePaste(properties, panelId),
            BuiltInAgentTools.TerminalSendKeys =>
                ParseSendKey(properties, panelId),
            BuiltInAgentTools.TerminalSendChord =>
                ParseSendChord(properties, panelId),
            BuiltInAgentTools.TerminalSendMouse =>
                ParseSendMouse(properties, panelId),
            BuiltInAgentTools.TerminalWait =>
                ParseWait(properties, panelId),
            BuiltInAgentTools.TerminalInterrupt =>
                ParseEmpty(
                    properties,
                    new TerminalAgentIntent.Interrupt(),
                    panelId),
            BuiltInAgentTools.TerminalResize =>
                ParseResize(properties, panelId),
            _ => UnknownTool(),
        };

    private static TerminalAgentIntentResult ParseEmpty(
        IReadOnlyDictionary<string, JsonElement> properties,
        TerminalAgentIntent intent,
        PanelInstanceId? panelId) =>
        properties.Count == 0
            ? new TerminalAgentIntentResult.Parsed(intent, panelId)
            : Invalid("This tool does not accept arguments.");

    private static TerminalAgentIntentResult ParseSendText(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedText(textElement, out var text))
        {
            return Invalid("Send text requires one bounded printable text field.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendText(text),
            panelId);
    }

    private static TerminalAgentIntentResult ParsePaste(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedPasteText(textElement, out var text))
        {
            return Invalid(
                "Paste requires one bounded text field; only tabs and line breaks may be control characters.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.Paste(text),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSendKey(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count is < 1 or > 2
            || !properties.TryGetValue("key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String
            || !TryParseKey(keyElement.GetString(), out var key))
        {
            return Invalid("Send keys requires one supported key and optional modifiers.");
        }

        var modifiers = TerminalKeyModifiers.None;
        if (properties.TryGetValue("modifiers", out var modifiersElement)
            && !TryParseModifiers(modifiersElement, out modifiers))
        {
            return Invalid("Terminal key modifiers are invalid.");
        }

        if (properties.Keys.Any(name => name is not ("key" or "modifiers")))
        {
            return Invalid("Send keys contains an unknown field.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendKey(
                new TerminalKeyStroke(key, modifiers)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSendChord(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !properties.TryGetValue("character", out var characterElement)
            || characterElement.ValueKind != JsonValueKind.String
            || characterElement.GetString() is not { Length: 1 } characterText
            || characterText[0] is < 'a' or > 'z'
            || !properties.TryGetValue("modifier", out var modifierElement)
            || modifierElement.ValueKind != JsonValueKind.String
            || !TryParseChordModifier(
                modifierElement.GetString(),
                out var modifier))
        {
            return Invalid(
                "Send chord requires one lowercase ASCII letter and exactly one control or alt modifier.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendChord(
                new TerminalCharacterChord(characterText[0], modifier)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSendMouse(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count is < 3 or > 4
            || !properties.TryGetValue("event", out var eventElement)
            || eventElement.ValueKind != JsonValueKind.String
            || !TryParseMouseEvent(
                eventElement.GetString(),
                out var button,
                out var kind)
            || !TryReadMouseCoordinate(properties, "column", out var column)
            || !TryReadMouseCoordinate(properties, "row", out var row)
            || properties.Keys.Any(
                name => name is not ("event" or "column" or "row" or "modifiers")))
        {
            return Invalid(
                "Send mouse requires one supported event and bounded zero-based cell coordinates.");
        }

        var modifiers = TerminalKeyModifiers.None;
        if (properties.TryGetValue("modifiers", out var modifiersElement)
            && !TryParseModifiers(modifiersElement, out modifiers))
        {
            return Invalid("Terminal mouse modifiers are invalid.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendMouse(
                new TerminalMouseInput(
                    button,
                    kind,
                    column,
                    row,
                    modifiers)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseResize(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !TryReadGridDimension(properties, "columns", out var columns)
            || !TryReadGridDimension(properties, "rows", out var rows))
        {
            return Invalid(
                "Terminal resize requires columns between 2 and 1000 "
                + "and rows between 1 and 1000.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.Resize(columns, rows),
            panelId);
    }

    private static TerminalAgentIntentResult ParseWait(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !properties.TryGetValue("timeout_ms", out var timeoutElement)
            || timeoutElement.ValueKind != JsonValueKind.Number
            || !timeoutElement.TryGetInt32(out var timeoutMilliseconds)
            || timeoutMilliseconds < 1
            || timeoutMilliseconds > MaximumWait.TotalMilliseconds)
        {
            return Invalid(
                "Terminal wait requires one bounded condition and a timeout up to 30 seconds.");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        if (properties.TryGetValue("text", out var textElement))
        {
            if (!TryReadBoundedText(textElement, out var text))
            {
                return Invalid(
                    "Terminal text wait requires bounded printable text.");
            }

            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.WaitForText(text, timeout),
                panelId);
        }

        if (properties.TryGetValue(
                "after_content_revision",
                out var revisionElement))
        {
            if (revisionElement.ValueKind != JsonValueKind.Number
                || !revisionElement.TryGetInt64(out var revision)
                || revision < 0)
            {
                return Invalid(
                    "Terminal change wait requires a non-negative content revision.");
            }

            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.WaitForChange(revision, timeout),
                panelId);
        }

        if (properties.TryGetValue("stable_for_ms", out var stableElement))
        {
            if (stableElement.ValueKind != JsonValueKind.Number
                || !stableElement.TryGetInt32(out var stableMilliseconds)
                || stableMilliseconds < 1
                || stableMilliseconds > timeoutMilliseconds)
            {
                return Invalid(
                    "Terminal stable wait requires a positive interval no longer than its timeout.");
            }

            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.WaitForStable(
                    TimeSpan.FromMilliseconds(stableMilliseconds),
                    timeout),
                panelId);
        }

        return Invalid(
            "Terminal wait requires text, after_content_revision, or stable_for_ms.");
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

    private static bool TryReadBoundedText(
        JsonElement element,
        out string text)
    {
        text = string.Empty;
        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
        {
            return false;
        }

        var value = element.GetString()!;
        if (Encoding.UTF8.GetByteCount(value) > MaximumTextBytes
            || value.Any(char.IsControl))
        {
            return false;
        }

        text = string.Concat(value);
        return true;
    }

    private static bool TryReadBoundedPasteText(
        JsonElement element,
        out string text)
    {
        text = string.Empty;
        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
        {
            return false;
        }

        var value = element.GetString()!;
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        if (byteCount > MaximumTextBytes
            || value.Any(character =>
                char.IsControl(character)
                && character is not ('\t' or '\r' or '\n')))
        {
            return false;
        }

        text = string.Concat(value);
        return true;
    }

    private static bool TryReadMouseCoordinate(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out int coordinate)
    {
        coordinate = 0;
        return properties.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out coordinate)
            && coordinate is >= 0 and <= 1_000_000;
    }

    private static bool TryReadGridDimension(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out int dimension)
    {
        dimension = 0;
        var minimum = name == "columns" ? MinimumGridColumns : 1;
        return properties.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out dimension)
            && dimension >= minimum
            && dimension <= MaximumGridDimension;
    }

    private static bool TryParseMouseEvent(
        string? value,
        out TerminalMouseButton button,
        out TerminalMouseEventKind kind)
    {
        (button, kind) = value switch
        {
            "move" => (
                TerminalMouseButton.None,
                TerminalMouseEventKind.Move),
            "left_down" => (
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down),
            "left_up" => (
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Up),
            "left_drag" => (
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Drag),
            "middle_down" => (
                TerminalMouseButton.Middle,
                TerminalMouseEventKind.Down),
            "middle_up" => (
                TerminalMouseButton.Middle,
                TerminalMouseEventKind.Up),
            "middle_drag" => (
                TerminalMouseButton.Middle,
                TerminalMouseEventKind.Drag),
            "right_down" => (
                TerminalMouseButton.Right,
                TerminalMouseEventKind.Down),
            "right_up" => (
                TerminalMouseButton.Right,
                TerminalMouseEventKind.Up),
            "right_drag" => (
                TerminalMouseButton.Right,
                TerminalMouseEventKind.Drag),
            "wheel_up" => (
                TerminalMouseButton.WheelUp,
                TerminalMouseEventKind.WheelUp),
            "wheel_down" => (
                TerminalMouseButton.WheelDown,
                TerminalMouseEventKind.WheelDown),
            _ => default,
        };
        return value is
            "move"
            or "left_down" or "left_up" or "left_drag"
            or "middle_down" or "middle_up" or "middle_drag"
            or "right_down" or "right_up" or "right_drag"
            or "wheel_up" or "wheel_down";
    }

    private static bool TryParseModifiers(
        JsonElement element,
        out TerminalKeyModifiers modifiers)
    {
        modifiers = TerminalKeyModifiers.None;
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var count = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (++count > 4 || item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var modifier = item.GetString() switch
            {
                "shift" => TerminalKeyModifiers.Shift,
                "alt" => TerminalKeyModifiers.Alt,
                "control" => TerminalKeyModifiers.Control,
                "meta" => TerminalKeyModifiers.Meta,
                _ => (TerminalKeyModifiers?)null,
            };
            if (modifier is null || (modifiers & modifier.Value) != 0)
            {
                return false;
            }

            modifiers |= modifier.Value;
        }

        return true;
    }

    private static bool TryParseKey(string? value, out TerminalKey key)
    {
        key = value switch
        {
            "enter" => TerminalKey.Enter,
            "tab" => TerminalKey.Tab,
            "backspace" => TerminalKey.Backspace,
            "escape" => TerminalKey.Escape,
            "space" => TerminalKey.Space,
            "up" => TerminalKey.Up,
            "down" => TerminalKey.Down,
            "left" => TerminalKey.Left,
            "right" => TerminalKey.Right,
            "home" => TerminalKey.Home,
            "end" => TerminalKey.End,
            "page_up" => TerminalKey.PageUp,
            "page_down" => TerminalKey.PageDown,
            "insert" => TerminalKey.Insert,
            "delete" => TerminalKey.Delete,
            "f1" => TerminalKey.F1,
            "f2" => TerminalKey.F2,
            "f3" => TerminalKey.F3,
            "f4" => TerminalKey.F4,
            "f5" => TerminalKey.F5,
            "f6" => TerminalKey.F6,
            "f7" => TerminalKey.F7,
            "f8" => TerminalKey.F8,
            "f9" => TerminalKey.F9,
            "f10" => TerminalKey.F10,
            "f11" => TerminalKey.F11,
            "f12" => TerminalKey.F12,
            "f13" => TerminalKey.F13,
            "f14" => TerminalKey.F14,
            "f15" => TerminalKey.F15,
            "f16" => TerminalKey.F16,
            "f17" => TerminalKey.F17,
            "f18" => TerminalKey.F18,
            "f19" => TerminalKey.F19,
            "f20" => TerminalKey.F20,
            _ => default,
        };
        return value is
            "enter" or "tab" or "backspace" or "escape" or "space"
            or "up" or "down" or "left" or "right" or "home" or "end"
            or "page_up" or "page_down" or "insert" or "delete"
            or "f1" or "f2" or "f3" or "f4" or "f5" or "f6" or "f7"
            or "f8" or "f9" or "f10" or "f11" or "f12" or "f13" or "f14"
            or "f15" or "f16" or "f17" or "f18" or "f19" or "f20";
    }

    private static bool TryParseChordModifier(
        string? value,
        out TerminalCharacterChordModifier modifier)
    {
        modifier = value switch
        {
            "control" => TerminalCharacterChordModifier.Control,
            "alt" => TerminalCharacterChordModifier.Alt,
            _ => default,
        };
        return value is "control" or "alt";
    }

    private static bool IsKnownTool(string toolName) =>
        toolName is
            BuiltInAgentTools.TerminalReadScreen
            or BuiltInAgentTools.TerminalSendText
            or BuiltInAgentTools.TerminalPaste
            or BuiltInAgentTools.TerminalSendKeys
            or BuiltInAgentTools.TerminalSendChord
            or BuiltInAgentTools.TerminalSendMouse
            or BuiltInAgentTools.TerminalWait
            or BuiltInAgentTools.TerminalInterrupt
            or BuiltInAgentTools.TerminalResize;

    private static TerminalAgentIntentResult UnknownTool() =>
        new TerminalAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a tool that is not available to this run.");

    private static TerminalAgentIntentResult UnavailableTool() =>
        new TerminalAgentIntentResult.Rejected(
            "tool_not_available",
            "The terminal tool is not available in the freshly resolved scope.");

    private static TerminalAgentIntentResult Invalid(string message) =>
        new TerminalAgentIntentResult.Rejected("invalid_tool_arguments", message);
}
