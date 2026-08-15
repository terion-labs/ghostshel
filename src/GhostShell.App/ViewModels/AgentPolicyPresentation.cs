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
            ? "Full access"
            : permission.ToString();

    public static string CapabilityDescription(AgentCapability capability) =>
        capability switch
        {
            AgentCapability.TerminalRead => "Read visible terminal output.",
            AgentCapability.RunCommands => "Send commands to terminal sessions.",
            AgentCapability.EditFiles => "Create, modify, and delete files.",
            AgentCapability.ReadFiles => "Read files and list directories.",
            AgentCapability.Search => "Search file contents and paths.",
            AgentCapability.Git => "Inspect and change Git repositories.",
            AgentCapability.WebFetch => "Fetch internet resources.",
            AgentCapability.Docker => "Inspect and manage Docker workloads.",
            AgentCapability.DestructiveTerminalActions =>
                "Run terminal actions classified as destructive.",
            AgentCapability.BrowserNavigation => "Navigate browser panels.",
            AgentCapability.BrowserData => "Read browser page state.",
            AgentCapability.BrowserInteraction => "Interact with page controls.",
            AgentCapability.ProcessControl => "Read or control local processes and statistics.",
            AgentCapability.McpTools => "Use tools exposed by configured MCP servers.",
            AgentCapability.SecretUse => "Resolve approved secret references for tools.",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };
}
