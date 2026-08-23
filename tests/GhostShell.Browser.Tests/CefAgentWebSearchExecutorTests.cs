using GhostShell.Application;

namespace GhostShell.Browser.Tests;

public sealed class CefAgentWebSearchExecutorTests
{
    [Fact]
    public void SearchAddressUsesOnlyFixedGoogleParametersAndEncodedQuery()
    {
        var address = CefAgentWebSearchExecutor.CreateSearchAddress(
            new AgentWebSearchRequest("cef search & safety", 4));

        Assert.Equal(Uri.UriSchemeHttps, address.Value.Scheme);
        Assert.Equal("www.google.com", address.Value.IdnHost);
        Assert.Equal("/search", address.Value.AbsolutePath);
        Assert.Equal(
            "?q=cef%20search%20%26%20safety&num=4&hl=en&pws=0",
            address.Value.Query);
        Assert.Empty(address.Value.UserInfo);
        Assert.Empty(address.Value.Fragment);
    }

    [Fact]
    public void ExtractionConvertsTheRsoFragmentToMarkdown()
    {
        const string json = """
            {
              "title": "Search results",
              "pageText": "Google navigation Useful result Sign in",
              "html": "<div id='rso'><h2>Useful result</h2><a href='https://example.test/docs'>Example docs</a></div>",
              "truncated": false
            }
            """;

        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri("https://www.google.com/search?q=cef")),
            json);

        var succeeded = Assert.IsType<AgentWebSearchExecutionResult.Succeeded>(
            result);
        Assert.Equal("Search results", succeeded.Result.Title);
        Assert.Contains("## Useful result", succeeded.Result.Text, StringComparison.Ordinal);
        Assert.Contains(
            "[Example docs](https://example.test/docs)",
            succeeded.Result.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Sign in", succeeded.Result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.google.com/sorry/index", "Search", "")]
    [InlineData("https://consent.google.com/", "Google", "Before you continue to Google")]
    [InlineData("https://www.google.com/search?q=cef", "Unusual traffic", "")]
    public void GoogleInterstitialIsNotReturnedAsSearchContent(
        string address,
        string title,
        string text)
    {
        var json = $$"""
            {
              "title": "{{title}}",
              "pageText": "{{text}}",
              "html": "",
              "truncated": false
            }
            """;

        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri(address)),
            json);

        var failed = Assert.IsType<AgentWebSearchExecutionResult.Failed>(result);
        Assert.Equal(AgentWebSearchErrorCode.Interstitial, failed.Code);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"title\":\"Search\",\"pageText\":\"x\",\"html\":\"\",\"truncated\":false}")]
    public void MalformedOrMissingRsoExtractionFailsClosed(string json)
    {
        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri("https://www.google.com/search?q=cef")),
            json);

        var failed = Assert.IsType<AgentWebSearchExecutionResult.Failed>(result);
        Assert.Equal(AgentWebSearchErrorCode.ExtractionFailed, failed.Code);
    }

}
