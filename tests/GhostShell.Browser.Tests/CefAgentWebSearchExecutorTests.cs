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
    public void ExtractionProjectsBoundedHttpLinks()
    {
        const string json = """
            {
              "title": "Search results",
              "text": "Useful result",
              "truncated": false,
              "links": [
                { "text": "Example", "url": "https://example.test/docs" },
                { "text": "HTTP", "url": "http://example.org/" }
              ]
            }
            """;

        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri("https://www.google.com/search?q=cef")),
            json);

        var succeeded = Assert.IsType<AgentWebSearchExecutionResult.Succeeded>(
            result);
        Assert.Equal("Search results", succeeded.Result.Title);
        Assert.Equal(2, succeeded.Result.Links.Count);
        Assert.Equal(
            "https://example.test/docs",
            succeeded.Result.Links[0].Url);
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
              "text": "{{text}}",
              "truncated": false,
              "links": []
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
    [InlineData("{\"title\":\"Search\",\"text\":\"x\",\"truncated\":false,\"links\":[{\"text\":\"bad\",\"url\":\"file:///tmp/private\"}]}")]
    public void MalformedOrUnsafeExtractionFailsClosed(string json)
    {
        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri("https://www.google.com/search?q=cef")),
            json);

        var failed = Assert.IsType<AgentWebSearchExecutionResult.Failed>(result);
        Assert.Equal(AgentWebSearchErrorCode.ExtractionFailed, failed.Code);
    }
}
