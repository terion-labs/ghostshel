using System.Text;

namespace GhostShell.Application;

public sealed record AgentWebSearchLink
{
    public const int MaximumTextBytes = 256;
    public const int MaximumUrlBytes = 2_048;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentWebSearchLink(string text, string url)
    {
        Text = RequireText(text, MaximumTextBytes, nameof(text));
        Url = RequireHttpUrl(url);
    }

    public string Text { get; }

    public string Url { get; }

    private static string RequireHttpUrl(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!TryGetByteCount(value, out var byteCount)
            || byteCount > MaximumUrlBytes
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(uri.Scheme.Equals(
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "A web search result URL must be a bounded credential-free HTTP(S) URL.",
                nameof(value));
        }

        return uri.AbsoluteUri;
    }

    internal static string RequireText(
        string value,
        int maximumBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal)
            || !TryGetByteCount(value, out var byteCount)
            || byteCount > maximumBytes)
        {
            throw new ArgumentException(
                "Web search text is invalid or exceeds its byte limit.",
                parameterName);
        }

        return string.Concat(value);
    }

    private static bool TryGetByteCount(string value, out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }
}

public sealed record AgentWebSearchResult : AgentWebToolResult
{
    public const int MaximumTitleBytes = 1_024;
    public const int MaximumTextBytes = 20 * 1_024;
    public const int MaximumLinks = 30;

    public AgentWebSearchResult(
        string finalUrl,
        string title,
        string text,
        IReadOnlyList<AgentWebSearchLink> links,
        bool truncated)
    {
        FinalUrl = new AgentWebSearchLink("result", finalUrl).Url;
        Title = AgentWebSearchLink.RequireText(
            title,
            MaximumTitleBytes,
            nameof(title));
        Text = AgentWebSearchLink.RequireText(
            text,
            MaximumTextBytes,
            nameof(text));
        ArgumentNullException.ThrowIfNull(links);
        if (links.Count > MaximumLinks || links.Any(link => link is null))
        {
            throw new ArgumentException(
                $"A web search result may contain at most {MaximumLinks} links.",
                nameof(links));
        }

        Links = Array.AsReadOnly([.. links]);
        Truncated = truncated;
    }

    public string FinalUrl { get; }

    public string Title { get; }

    public string Text { get; }

    public IReadOnlyList<AgentWebSearchLink> Links { get; }

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
