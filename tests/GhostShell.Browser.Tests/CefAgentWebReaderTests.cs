using GhostShell.Application;

namespace GhostShell.Browser.Tests;

public sealed class CefAgentWebReaderTests
{
    [Fact]
    public async Task MarkdownModeRunsReadabilityAndHtmlConversion()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat(
            "GhostSHELL renders documentation in an isolated Chromium page.",
            20));
        var json = $$"""
            {
              "title": "Original title",
              "html": "<html><head><title>Reader guide</title></head><body><nav>Menu</nav><main><h1>Reader guide</h1><p>{{paragraph}}</p></main><script>secret()</script></body></html>",
              "truncated": false
            }
            """;

        var result = await new CefAgentWebReader().ConvertAsync(
            new BrowserAddress(new Uri("https://docs.example.test/guide")),
            AgentWebReadFormat.Markdown,
            json,
            CancellationToken.None);

        var succeeded = Assert.IsType<AgentWebToolExecutionResult.Succeeded>(result);
        var read = Assert.IsType<AgentWebReadResult>(succeeded.Result);
        Assert.Equal(AgentWebReadFormat.Markdown, read.Format);
        Assert.Equal("Reader guide", read.Title);
        Assert.Contains(
            "GhostSHELL renders documentation",
            read.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("secret()", read.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Menu", read.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderedHtmlModeReturnsExplicitBoundedDom()
    {
        const string json = """
            {
              "title": "Dynamic page",
              "html": "<html><body><main>Rendered client content</main></body></html>",
              "truncated": true
            }
            """;

        var result = await new CefAgentWebReader().ConvertAsync(
            new BrowserAddress(new Uri("https://app.example.test/")),
            AgentWebReadFormat.RenderedHtml,
            json,
            CancellationToken.None);

        var succeeded = Assert.IsType<AgentWebToolExecutionResult.Succeeded>(result);
        var read = Assert.IsType<AgentWebReadResult>(succeeded.Result);
        Assert.Equal("Dynamic page", read.Title);
        Assert.Contains("Rendered client content", read.Content, StringComparison.Ordinal);
        Assert.True(read.Truncated);
    }
}
