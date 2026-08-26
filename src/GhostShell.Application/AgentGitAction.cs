namespace GhostShell.Application;

public sealed record AgentGitAction(
    AgentGitRequest Request,
    AgentActionProposal Proposal);
