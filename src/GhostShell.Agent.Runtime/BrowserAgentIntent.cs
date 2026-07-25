using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal abstract record BrowserAgentIntent
{
    private BrowserAgentIntent()
    {
    }

    public sealed record ReadState : BrowserAgentIntent;

    public sealed record Snapshot : BrowserAgentIntent;

    public sealed record Click(
        BrowserElementReferenceId Reference,
        long DocumentRevision) : BrowserAgentIntent;

    public sealed record Fill(
        BrowserElementReferenceId Reference,
        long DocumentRevision,
        string Text) : BrowserAgentIntent;

    public sealed record Check(
        BrowserElementReferenceId Reference,
        long DocumentRevision) : BrowserAgentIntent;

    public sealed record Navigate(BrowserAddress Address) : BrowserAgentIntent;

    public sealed record Back : BrowserAgentIntent;

    public sealed record Forward : BrowserAgentIntent;

    public sealed record Reload : BrowserAgentIntent;

    public sealed record Stop : BrowserAgentIntent;
}

internal abstract record BrowserAgentIntentResult
{
    private BrowserAgentIntentResult()
    {
    }

    public sealed record Parsed(
        BrowserAgentIntent Intent,
        PanelInstanceId? PanelId = null)
        : BrowserAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : BrowserAgentIntentResult;
}
