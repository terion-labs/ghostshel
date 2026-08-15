using System.Collections.ObjectModel;
using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AgentToolDescriptor
{
    public AgentToolDescriptor(
        string name,
        string title,
        AgentCapability capability,
        AgentActionRisk risk)
    {
        Name = RequireToolName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 128 || title.Any(char.IsControl))
        {
            throw new ArgumentException("An agent tool title is invalid.", nameof(title));
        }

        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        Title = title;
        Capability = capability;
        Risk = risk;
    }

    public string Name { get; }

    public string Title { get; }

    public AgentCapability Capability { get; }

    public AgentActionRisk Risk { get; }

    private static string RequireToolName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value[0] == '.'
            || value[^1] == '.'
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '.'
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException(
                "An agent tool name must be a bounded lowercase identifier.",
                nameof(value));
        }

        return value;
    }
}

public sealed class AgentToolCatalog
{
    private readonly IReadOnlyDictionary<string, AgentToolDescriptor> _tools;

    public AgentToolCatalog(IEnumerable<AgentToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var values = tools
            .Select(tool => tool ?? throw new ArgumentException(
                "An agent tool catalog cannot contain null entries.",
                nameof(tools)))
            .ToArray();
        var dictionary = new Dictionary<string, AgentToolDescriptor>(StringComparer.Ordinal);
        foreach (var tool in values)
        {
            if (!dictionary.TryAdd(tool.Name, tool))
            {
                throw new ArgumentException(
                    $"Agent tool '{tool.Name}' is registered more than once.",
                    nameof(tools));
            }
        }

        _tools = new ReadOnlyDictionary<string, AgentToolDescriptor>(dictionary);
        Tools = Array.AsReadOnly(values);
    }

    public IReadOnlyList<AgentToolDescriptor> Tools { get; }

    public bool TryGet(string name, out AgentToolDescriptor? descriptor) =>
        _tools.TryGetValue(name, out descriptor);
}

public static class BuiltInAgentTools
{
    public const string WorkspaceList = "workspace.list";
    public const string WorkspaceInspect = "workspace.inspect";
    public const string TabList = "tab.list";
    public const string PanelList = "panel.list";
    public const string PanelInspect = "panel.inspect";
    public const string PanelFocus = "panel.focus";
    public const string TerminalReadScreen = "terminal.read_screen";
    public const string TerminalSendText = "terminal.send_text";
    public const string TerminalPaste = "terminal.paste";
    public const string TerminalSendKeys = "terminal.send_keys";
    public const string TerminalSendChord = "terminal.send_chord";
    public const string TerminalSendMouse = "terminal.send_mouse";
    public const string TerminalWait = "terminal.wait";
    public const string TerminalInterrupt = "terminal.interrupt";
    public const string TerminalResize = "terminal.resize";
    public const string BrowserReadState = "browser.read_state";
    public const string BrowserSnapshot = "browser.snapshot";
    public const string BrowserClick = "browser.click";
    public const string BrowserFill = "browser.fill";
    public const string BrowserCheck = "browser.check";
    public const string BrowserNavigate = "browser.navigate";
    public const string BrowserBack = "browser.back";
    public const string BrowserForward = "browser.forward";
    public const string BrowserReload = "browser.reload";
    public const string BrowserStop = "browser.stop";
    public const string FilesList = "files.list";
    public const string FilesStat = "files.stat";
    public const string FilesRead = "files.read";
    public const string FilesCreateDirectory = "files.mkdir";
    public const string FilesDelete = "files.delete";
    public const string ProcessesList = "processes.list";
    public const string StatisticsRead = "statistics.read";
    public const string McpCall = "mcp.call";

    public static AgentToolCatalog Catalog { get; } = new(
    [
        Tool(WorkspaceList, "List workspaces", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(WorkspaceInspect, "Inspect workspace", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(TabList, "List tabs", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(PanelList, "List panels", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(PanelInspect, "Inspect panel", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(PanelFocus, "Focus panel", AgentCapability.RunCommands, AgentActionRisk.Routine),
        Tool(
            TerminalReadScreen,
            "Read terminal screen",
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation),
        Tool(
            TerminalSendText,
            "Send terminal text",
            AgentCapability.RunCommands,
            AgentActionRisk.Mutation),
        Tool(
            TerminalPaste,
            "Paste terminal text",
            AgentCapability.RunCommands,
            AgentActionRisk.Mutation),
        Tool(
            TerminalSendKeys,
            "Send terminal keys",
            AgentCapability.RunCommands,
            AgentActionRisk.Mutation),
        Tool(
            TerminalSendChord,
            "Send terminal character chord",
            AgentCapability.DestructiveTerminalActions,
            AgentActionRisk.Destructive),
        Tool(
            TerminalSendMouse,
            "Send terminal mouse event",
            AgentCapability.RunCommands,
            AgentActionRisk.Mutation),
        Tool(
            TerminalWait,
            "Wait for terminal state",
            AgentCapability.TerminalRead,
            AgentActionRisk.Routine),
        Tool(
            TerminalInterrupt,
            "Interrupt terminal process",
            AgentCapability.DestructiveTerminalActions,
            AgentActionRisk.Destructive),
        Tool(
            TerminalResize,
            "Resize terminal",
            AgentCapability.RunCommands,
            AgentActionRisk.Mutation),
        Tool(
            BrowserReadState,
            "Read browser state",
            AgentCapability.BrowserData,
            AgentActionRisk.Observation),
        Tool(
            BrowserSnapshot,
            "Capture browser snapshot",
            AgentCapability.BrowserData,
            AgentActionRisk.Observation),
        Tool(
            BrowserClick,
            "Click browser element",
            AgentCapability.BrowserInteraction,
            AgentActionRisk.Mutation),
        Tool(
            BrowserFill,
            "Fill browser element",
            AgentCapability.BrowserInteraction,
            AgentActionRisk.Mutation),
        Tool(
            BrowserCheck,
            "Check browser element",
            AgentCapability.BrowserInteraction,
            AgentActionRisk.Mutation),
        Tool(
            BrowserNavigate,
            "Navigate browser",
            AgentCapability.BrowserNavigation,
            AgentActionRisk.Mutation),
        Tool(
            BrowserBack,
            "Go back in browser",
            AgentCapability.BrowserNavigation,
            AgentActionRisk.Mutation),
        Tool(
            BrowserForward,
            "Go forward in browser",
            AgentCapability.BrowserNavigation,
            AgentActionRisk.Mutation),
        Tool(
            BrowserReload,
            "Reload browser",
            AgentCapability.BrowserNavigation,
            AgentActionRisk.Mutation),
        Tool(
            BrowserStop,
            "Stop browser loading",
            AgentCapability.BrowserNavigation,
            AgentActionRisk.Mutation),
        Tool(
            FilesList,
            "List files",
            AgentCapability.ReadFiles,
            AgentActionRisk.Observation),
        Tool(
            FilesStat,
            "Inspect file metadata",
            AgentCapability.ReadFiles,
            AgentActionRisk.Observation),
        Tool(
            FilesRead,
            "Read file preview",
            AgentCapability.ReadFiles,
            AgentActionRisk.Observation),
        Tool(
            FilesCreateDirectory,
            "Create directory",
            AgentCapability.EditFiles,
            AgentActionRisk.Mutation),
        Tool(
            FilesDelete,
            "Permanently delete path",
            AgentCapability.EditFiles,
            AgentActionRisk.Destructive),
        Tool(
            ProcessesList,
            "List local processes",
            AgentCapability.ProcessControl,
            AgentActionRisk.Observation),
        // Statistics and process listing are the two read-only local
        // system-monitor surfaces. Keeping them under the existing policy
        // capability avoids silently enabling a new persisted permission;
        // the distinct tool and session capability still authorize each
        // observation independently.
        Tool(
            StatisticsRead,
            "Read local system statistics",
            AgentCapability.ProcessControl,
            AgentActionRisk.Observation),
        Tool(
            McpCall,
            "Run MCP tool",
            AgentCapability.McpTools,
            AgentActionRisk.Mutation),
    ]);

    private static AgentToolDescriptor Tool(
        string name,
        string title,
        AgentCapability capability,
        AgentActionRisk risk) =>
        new(name, title, capability, risk);
}
