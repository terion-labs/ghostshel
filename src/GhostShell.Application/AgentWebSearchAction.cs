namespace GhostShell.Application;

public sealed class AgentWebSearchAction
{
    internal AgentWebSearchAction(
        AgentWebSearchRequest request,
        AgentActionProposal proposal)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public AgentWebSearchRequest Request { get; }

    public AgentActionProposal Proposal { get; }
}
