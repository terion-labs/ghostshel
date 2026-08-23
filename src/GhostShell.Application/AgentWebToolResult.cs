using System.Text;

namespace GhostShell.Application;

public abstract record AgentWebToolResult
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private protected AgentWebToolResult()
    {
    }

    internal static string RequireBoundedText(
        string value,
        int maximumBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        try
        {
            if (value.Contains('\0', StringComparison.Ordinal)
                || StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw new ArgumentException(
                    "Web content is invalid or exceeds its byte limit.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Web content is not valid UTF-8 text.", parameterName, exception);
        }

        return string.Concat(value);
    }

    internal static string RequireFinalUrl(string value) =>
        AgentWebToolRequest.RequireHttpAddress(value, nameof(value)).AbsoluteUri;
}

public sealed record AgentHttpFetchResult : AgentWebToolResult
{
    public const int MaximumContentBytes = 64 * 1_024;
    public const int MaximumMediaTypeBytes = 256;

    public AgentHttpFetchResult(
        string finalUrl,
        int statusCode,
        string mediaType,
        string content)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        FinalUrl = RequireFinalUrl(finalUrl);
        StatusCode = statusCode;
        MediaType = RequireBoundedText(
            mediaType,
            MaximumMediaTypeBytes,
            nameof(mediaType));
        Content = RequireBoundedText(
            content,
            MaximumContentBytes,
            nameof(content));
    }

    public string FinalUrl { get; }

    public int StatusCode { get; }

    public string MediaType { get; }

    public string Content { get; }
}

public sealed record AgentWebReadResult : AgentWebToolResult
{
    public const int MaximumTitleBytes = 1_024;
    public const int MaximumContentBytes = 64 * 1_024;

    public AgentWebReadResult(
        string finalUrl,
        string title,
        AgentWebReadFormat format,
        string content,
        bool truncated)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        FinalUrl = RequireFinalUrl(finalUrl);
        Title = RequireBoundedText(title, MaximumTitleBytes, nameof(title));
        Format = format;
        Content = RequireBoundedText(content, MaximumContentBytes, nameof(content));
        Truncated = truncated;
    }

    public string FinalUrl { get; }

    public string Title { get; }

    public AgentWebReadFormat Format { get; }

    public string Content { get; }

    public bool Truncated { get; }
}

public enum AgentWebToolErrorCode
{
    Unavailable,
    InvalidUrl,
    DestinationDenied,
    DnsFailed,
    RedirectLimit,
    TimedOut,
    BodyTooLarge,
    UnsupportedContentType,
    LoadFailed,
    RenderProcessFailed,
    ExtractionFailed,
    ConverterFailed,
    SearchInterstitial,
    Cancelled,
}

public abstract record AgentWebToolExecutionResult
{
    private AgentWebToolExecutionResult()
    {
    }

    public sealed record Succeeded(AgentWebToolResult Result)
        : AgentWebToolExecutionResult;

    public sealed record Failed(AgentWebToolErrorCode Code)
        : AgentWebToolExecutionResult;
}
