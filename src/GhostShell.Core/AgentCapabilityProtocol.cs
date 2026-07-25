namespace GhostShell.Core;

/// <summary>
/// Stable provider-protocol tokens for agent capabilities. These values are
/// intentionally independent of CLR enum names so routine code renames cannot
/// silently change the provider contract.
/// </summary>
public static class AgentCapabilityProtocol
{
    public const string TerminalRead = "terminal_read";
    public const string RunCommands = "run_commands";
    public const string EditFiles = "edit_files";
    public const string ReadFiles = "read_files";
    public const string Search = "search";
    public const string Git = "git";
    public const string WebFetch = "web_fetch";
    public const string Docker = "docker";
    public const string DestructiveTerminalActions =
        "destructive_terminal_actions";
    public const string BrowserNavigation = "browser_navigation";
    public const string BrowserData = "browser_data";
    public const string ProcessControl = "process_control";
    public const string McpTools = "mcp_tools";
    public const string SecretUse = "secret_use";
    public const string BrowserInteraction = "browser_interaction";

    public static string GetToken(AgentCapability capability) =>
        capability switch
        {
            AgentCapability.TerminalRead => TerminalRead,
            AgentCapability.RunCommands => RunCommands,
            AgentCapability.EditFiles => EditFiles,
            AgentCapability.ReadFiles => ReadFiles,
            AgentCapability.Search => Search,
            AgentCapability.Git => Git,
            AgentCapability.WebFetch => WebFetch,
            AgentCapability.Docker => Docker,
            AgentCapability.DestructiveTerminalActions =>
                DestructiveTerminalActions,
            AgentCapability.BrowserNavigation => BrowserNavigation,
            AgentCapability.BrowserData => BrowserData,
            AgentCapability.ProcessControl => ProcessControl,
            AgentCapability.McpTools => McpTools,
            AgentCapability.SecretUse => SecretUse,
            AgentCapability.BrowserInteraction => BrowserInteraction,
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    public static bool TryParseToken(
        string? token,
        out AgentCapability capability)
    {
        switch (token)
        {
            case TerminalRead:
                capability = AgentCapability.TerminalRead;
                return true;
            case RunCommands:
                capability = AgentCapability.RunCommands;
                return true;
            case EditFiles:
                capability = AgentCapability.EditFiles;
                return true;
            case ReadFiles:
                capability = AgentCapability.ReadFiles;
                return true;
            case Search:
                capability = AgentCapability.Search;
                return true;
            case Git:
                capability = AgentCapability.Git;
                return true;
            case WebFetch:
                capability = AgentCapability.WebFetch;
                return true;
            case Docker:
                capability = AgentCapability.Docker;
                return true;
            case DestructiveTerminalActions:
                capability = AgentCapability.DestructiveTerminalActions;
                return true;
            case BrowserNavigation:
                capability = AgentCapability.BrowserNavigation;
                return true;
            case BrowserData:
                capability = AgentCapability.BrowserData;
                return true;
            case ProcessControl:
                capability = AgentCapability.ProcessControl;
                return true;
            case McpTools:
                capability = AgentCapability.McpTools;
                return true;
            case SecretUse:
                capability = AgentCapability.SecretUse;
                return true;
            case BrowserInteraction:
                capability = AgentCapability.BrowserInteraction;
                return true;
            default:
                capability = default;
                return false;
        }
    }
}
