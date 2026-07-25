using GhostShell.Core;

namespace GhostShell.App.ViewModels;

internal static class AgentPolicyPresentation
{
    public static string CapabilityName(AgentCapability capability) =>
        capability switch
        {
            AgentCapability.TerminalRead => "Terminal read",
            AgentCapability.RunCommands => "Run commands",
            AgentCapability.EditFiles => "Edit files",
            AgentCapability.ReadFiles => "Read files",
            AgentCapability.Search => "Search",
            AgentCapability.Git => "Git",
            AgentCapability.WebFetch => "Web fetch",
            AgentCapability.Docker => "Docker",
            AgentCapability.DestructiveTerminalActions => "Destructive terminal actions",
            AgentCapability.BrowserNavigation => "Browser navigation",
            AgentCapability.BrowserData => "Browser data",
            AgentCapability.ProcessControl => "Process control",
            AgentCapability.McpTools => "MCP tools",
            AgentCapability.SecretUse => "Secret use",
            AgentCapability.BrowserInteraction => "Browser interaction",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    public static string PermissionName(AgentPermission permission) =>
        permission == AgentPermission.Yolo
            ? "YOLO"
            : permission.ToString();
}
