namespace GhostShell.Application;

public sealed record AgentWebSearchResult : AgentWebToolResult
{
    public const int MaximumTitleBytes = 1_024;
    public const int MaximumTextBytes = 20 * 1_024;

    public AgentWebSearchResult(
        string finalUrl,
        string title,
        string text,
        bool truncated)
    {
        FinalUrl = RequireFinalUrl(finalUrl);
        Title = RequireBoundedText(title, MaximumTitleBytes, nameof(title));
        Text = RequireBoundedText(text, MaximumTextBytes, nameof(text));
        Truncated = truncated;
    }

    public string FinalUrl { get; }

    public string Title { get; }

    public string Text { get; }

    public bool Truncated { get; }
}

public enum AgentWebSearchErrorCode
{
    Unavailable,
    NavigationDenied,
    LoadFailed,
    Interstitial,
    ExtractionFailed,
    TimedOut,
    Cancelled,
}

public abstract record AgentWebSearchExecutionResult
{
    private AgentWebSearchExecutionResult()
    {
    }

    public sealed record Succeeded(AgentWebSearchResult Result)
        : AgentWebSearchExecutionResult;

    public sealed record Failed(AgentWebSearchErrorCode Code)
        : AgentWebSearchExecutionResult;
}
