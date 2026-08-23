using System.Text.Json;
using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime.Tests;

public sealed class WebSearchAgentToolResultJsonTests
{
    [Fact]
    public void SuccessIsLabeledUntrustedAndLimitsLinksToRequestedCount()
    {
        var request = new AgentWebSearchRequest("cef offscreen", 2);
        var result = new AgentWebSearchResult(
            "https://www.google.com/search?q=cef%20offscreen",
            "cef offscreen - Google Search",
            "Search page text",
            [
                new AgentWebSearchLink("First", "https://example.test/first"),
                new AgentWebSearchLink("Second", "https://example.test/second"),
                new AgentWebSearchLink("Third", "https://example.test/third"),
            ],
            truncated: true);

        var projection = WebSearchAgentToolResultJson.Project(request, result);

        Assert.True(projection.IsSuccess);
        Assert.Equal("web_search_completed", projection.StableCode);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("untrusted_web", root.GetProperty("content_origin").GetString());
        Assert.Equal("google", root.GetProperty("provider").GetString());
        Assert.Equal(request.Query, root.GetProperty("query").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("links").GetArrayLength());
    }

    [Theory]
    [InlineData(HostErrorCode.DeadlineExceeded, null, "web_search_timed_out")]
    [InlineData(HostErrorCode.Cancelled, null, "web_search_cancelled")]
    [InlineData(HostErrorCode.InvalidRequest, null, "target_changed")]
    [InlineData(HostErrorCode.EngineFailed, "web_search_interstitial", "web_search_interstitial")]
    [InlineData(HostErrorCode.EngineFailed, "engine-secret", "web_search_failed")]
    public void FailureUsesOnlyStablePublicCodes(
        HostErrorCode code,
        string? stableCode,
        string expected)
    {
        var error = stableCode is null
            ? HostError.Create(code, "engine-secret")
            : new HostError(code, stableCode, "engine-secret");

        var json = WebSearchAgentToolResultJson.Failure(error);

        Assert.DoesNotContain("engine-secret", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            expected,
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }
}
