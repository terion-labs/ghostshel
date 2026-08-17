using System.Collections.ObjectModel;
using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AgentToolDescriptor
{
    public static readonly TimeSpan DefaultMaximumExecutionLifetime =
        TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AbsoluteMaximumExecutionLifetime =
        TimeSpan.FromMinutes(61);

    public AgentToolDescriptor(
        string name,
        string title,
        AgentCapability capability,
        AgentActionRisk risk,
        TimeSpan? maximumExecutionLifetime = null)
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

        var executionLifetime = maximumExecutionLifetime
            ?? DefaultMaximumExecutionLifetime;
        if (executionLifetime <= TimeSpan.Zero
            || executionLifetime > AbsoluteMaximumExecutionLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExecutionLifetime));
        }

        Title = title;
        Capability = capability;
        Risk = risk;
        MaximumExecutionLifetime = executionLifetime;
    }

    public string Name { get; }

    public string Title { get; }

    public AgentCapability Capability { get; }

    public AgentActionRisk Risk { get; }

    /// <summary>
    /// Maximum lifetime after a one-action authorization is consumed. The
    /// unconsumed authorization remains independently short-lived.
    /// </summary>
    public TimeSpan MaximumExecutionLifetime { get; }

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
    public const string WorkspaceInspect = "workspace.inspect";
    public const string ConnectionsList = "connections.list";
    public const string TabList = "tab.list";
    public const string TabCreate = "tab.create";
    public const string TabClose = "tab.close";
    public const string PanelList = "panel.list";
    public const string PanelInspect = "panel.inspect";
    public const string PanelFocus = "panel.focus";
    public const string PanelConnect = "panel.connect";
    public const string PanelAdd = "panel.add";
    public const string PanelSplit = "panel.split";
    public const string PanelClose = "panel.close";
    public const string TerminalReadScreen = "terminal.read_screen";
    public const string TerminalReadScreenDiff = "terminal.read_screen_diff";
    public const string TerminalReadScrollback = "terminal.read_scrollback";
    public const string TerminalFind = "terminal.find";
    public const string TerminalFindOnScreen = "terminal.find_on_screen";
    public const string TerminalScrollViewport = "terminal.scroll_viewport";
    public const string TerminalSendText = "terminal.send_text";
    public const string TerminalPaste = "terminal.paste";
    public const string TerminalSubmitText = "terminal.submit_text";
    public const string TerminalSendKeys = "terminal.send_keys";
    public const string TerminalSendChord = "terminal.send_chord";
    public const string TerminalSendMouse = "terminal.send_mouse";
    public const string TerminalWait = "terminal.wait";
    public const string TerminalInterrupt = "terminal.interrupt";
    public const string TerminalResize = "terminal.resize";
    public const string BrowserReadState = "browser.read_state";
    public const string BrowserSnapshot = "browser.snapshot";
    public const string BrowserWait = "browser.wait";
    public const string BrowserClick = "browser.click";
    public const string BrowserFill = "browser.fill";
    public const string BrowserCheck = "browser.check";
    public const string BrowserMouse = "browser.mouse";
    public const string BrowserKey = "browser.key";
    public const string BrowserScroll = "browser.scroll";
    public const string BrowserEvaluate = "browser.evaluate";
    public const string BrowserNavigate = "browser.navigate";
    public const string BrowserBack = "browser.back";
    public const string BrowserForward = "browser.forward";
    public const string BrowserReload = "browser.reload";
    public const string BrowserStop = "browser.stop";
    public const string FilesList = "files.list";
    public const string FilesSearch = "files.search";
    public const string FilesStat = "files.stat";
    public const string FilesRead = "files.read";
    public const string FilesAccessRead = "files.access_read";
    public const string FilesTransfers = "files.transfers";
    public const string FilesCreateDirectory = "files.mkdir";
    public const string FilesMove = "files.move";
    public const string FilesDelete = "files.delete";
    public const string ProcessesList = "processes.list";
    public const string StatisticsRead = "statistics.read";
    public const string DatabaseReadState = "database.read_state";
    public const string DatabaseListObjects = "database.list_objects";
    public const string DatabaseDescribeObject = "database.describe_object";
    public const string DatabaseReadTable = "database.read_table";
    public const string DatabaseSchemaGraph = "database.schema_graph";
    public const string RedisScan = "redis.scan";
    public const string RedisRead = "redis.read";
    public const string RedisListIndexes = "redis.list_indexes";
    public const string RedisSearch = "redis.search";
    public const string DockerReadState = "docker.read_state";
    public const string DockerInspect = "docker.inspect";
    public const string DockerLogs = "docker.logs";
    public const string DockerFilesList = "docker.files_list";
    public const string DockerFilesStat = "docker.files_stat";
    public const string DockerFileRead = "docker.file_read";
    public const string McpCall = "mcp.call";

    public static AgentToolCatalog Catalog { get; } = new(
    [
        Tool(WorkspaceInspect, "Inspect workspace", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(ConnectionsList, "List workspace connections", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(TabList, "List tabs", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(TabCreate, "Create tab", AgentCapability.WorkspaceLayout, AgentActionRisk.Mutation),
        Tool(TabClose, "Close tab", AgentCapability.WorkspaceLayout, AgentActionRisk.Destructive),
        Tool(PanelList, "List panels", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(PanelInspect, "Inspect panel", AgentCapability.Search, AgentActionRisk.Observation),
        Tool(PanelFocus, "Focus panel", AgentCapability.RunCommands, AgentActionRisk.Routine),
        Tool(PanelConnect, "Connect panel", AgentCapability.WorkspaceLayout, AgentActionRisk.Destructive),
        Tool(PanelAdd, "Add panel", AgentCapability.WorkspaceLayout, AgentActionRisk.Mutation),
        Tool(PanelSplit, "Split panel", AgentCapability.WorkspaceLayout, AgentActionRisk.Mutation),
        Tool(PanelClose, "Close panel", AgentCapability.WorkspaceLayout, AgentActionRisk.Destructive),
        Tool(
            TerminalReadScreen,
            "Read terminal screen",
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation),
        Tool(
            TerminalReadScreenDiff,
            "Read terminal screen changes",
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation),
        Tool(
            TerminalReadScrollback,
            "Read terminal scrollback",
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation),
        Tool(
            TerminalFind,
            "Find terminal history",
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation),
        Tool(
            TerminalFindOnScreen,
            "Find text on terminal screen",
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation),
        Tool(
            TerminalScrollViewport,
            "Scroll terminal viewport",
            AgentCapability.RunCommands,
            AgentActionRisk.Routine),
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
            TerminalSubmitText,
            "Submit terminal text",
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
            AgentActionRisk.Routine,
            TimeSpan.FromMinutes(61)),
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
            BrowserWait,
            "Wait for browser state",
            AgentCapability.BrowserData,
            AgentActionRisk.Routine,
            TimeSpan.FromMinutes(61)),
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
            BrowserMouse,
            "Send browser mouse input",
            AgentCapability.BrowserInteraction,
            AgentActionRisk.Mutation),
        Tool(
            BrowserKey,
            "Send browser key input",
            AgentCapability.BrowserInteraction,
            AgentActionRisk.Mutation),
        Tool(
            BrowserScroll,
            "Scroll browser viewport",
            AgentCapability.BrowserInteraction,
            AgentActionRisk.Mutation),
        Tool(
            BrowserEvaluate,
            "Evaluate bounded browser script",
            AgentCapability.BrowserScripting,
            AgentActionRisk.Privileged),
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
            FilesSearch,
            "Search file names",
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
            FilesAccessRead,
            "Read file access control",
            AgentCapability.ReadFiles,
            AgentActionRisk.Observation),
        Tool(
            FilesTransfers,
            "List file transfers",
            AgentCapability.ReadFiles,
            AgentActionRisk.Observation),
        Tool(
            FilesCreateDirectory,
            "Create directory",
            AgentCapability.EditFiles,
            AgentActionRisk.Mutation),
        Tool(
            FilesMove,
            "Move or rename path",
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
            AgentCapability.ProcessData,
            AgentActionRisk.Observation),
        Tool(
            StatisticsRead,
            "Read local system statistics",
            AgentCapability.SystemData,
            AgentActionRisk.Observation),
        Tool(
            DatabaseReadState,
            "Read database session state",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            DatabaseListObjects,
            "List database objects",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            DatabaseDescribeObject,
            "Describe database object",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            DatabaseReadTable,
            "Read database table",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            DatabaseSchemaGraph,
            "Read database schema graph",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            RedisScan,
            "Scan Redis keys",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            RedisRead,
            "Read Redis key",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            RedisListIndexes,
            "List Redis Search indexes",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            RedisSearch,
            "Search Redis index",
            AgentCapability.DatabaseRead,
            AgentActionRisk.Observation),
        Tool(
            DockerReadState,
            "Read Docker engine state",
            AgentCapability.DockerData,
            AgentActionRisk.Observation),
        Tool(
            DockerInspect,
            "Inspect Docker resource",
            AgentCapability.DockerData,
            AgentActionRisk.Observation),
        Tool(
            DockerLogs,
            "Read Docker container logs",
            AgentCapability.DockerData,
            AgentActionRisk.Observation),
        Tool(
            DockerFilesList,
            "List Docker resource files",
            AgentCapability.DockerData,
            AgentActionRisk.Observation),
        Tool(
            DockerFilesStat,
            "Inspect Docker resource file",
            AgentCapability.DockerData,
            AgentActionRisk.Observation),
        Tool(
            DockerFileRead,
            "Read Docker resource text file",
            AgentCapability.DockerData,
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
        AgentActionRisk risk,
        TimeSpan? maximumExecutionLifetime = null) =>
        new(name, title, capability, risk, maximumExecutionLifetime);
}
