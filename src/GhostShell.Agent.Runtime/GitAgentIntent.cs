using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal abstract record GitAgentIntentResult
{
    private GitAgentIntentResult()
    {
    }

    public sealed record Parsed(
        PanelInstanceId PanelId,
        AgentGitRequest Request) : GitAgentIntentResult;

    public sealed record Rejected(
        string StableCode,
        string Message) : GitAgentIntentResult;
}
