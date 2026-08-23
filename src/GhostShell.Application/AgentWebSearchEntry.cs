namespace GhostShell.Application;

public sealed record AgentWebSearchEntry
{
    public const int MaximumDescriptionBytes = 4 * 1_024;

    public AgentWebSearchEntry(string url, string description)
    {
        var boundedUrl = AgentWebToolResult.RequireBoundedText(
            url,
            AgentWebToolRequest.MaximumUrlBytes,
            nameof(url));
        if (!Uri.TryCreate(boundedUrl, UriKind.Absolute, out var address)
            || !(address.Scheme.Equals(
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                || address.Scheme.Equals(
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(address.Host)
            || !string.IsNullOrEmpty(address.UserInfo))
        {
            throw new ArgumentException(
                "A search result URL must be bounded, credential-free HTTP(S).",
                nameof(url));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Url = address.AbsoluteUri;
        Description = AgentWebToolResult.RequireBoundedText(
            description,
            MaximumDescriptionBytes,
            nameof(description));
    }

    public string Url { get; }

    public string Description { get; }
}
