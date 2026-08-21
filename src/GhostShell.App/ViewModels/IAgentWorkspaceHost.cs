namespace GhostShell.App.ViewModels;

/// <summary>
/// The typed presentation boundary shared by the main workspace and Quick
/// Terminal. Agent views compile against this contract instead of assuming a
/// concrete window data context and falling back to runtime member lookup.
/// </summary>
public interface IAgentWorkspaceHost
{
    AgentChatViewModel? AgentChat { get; }

    bool IsAgentPanelDocked { get; }

    string AgentPanelPinTip { get; }
}
