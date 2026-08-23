using GhostShell.Application;

namespace GhostShell.Application.Tests;

public sealed class AgentWebSearchResultTests
{
    [Fact]
    public void SearchEntryAcceptsHttpFragmentsAndNormalizesTheAddress()
    {
        var entry = new AgentWebSearchEntry(
            "https://EXAMPLE.test/docs#section",
            "Example documentation");

        Assert.Equal("https://example.test/docs#section", entry.Url);
        Assert.Equal("Example documentation", entry.Description);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.test/private")]
    [InlineData("file:///tmp/result")]
    public void SearchEntryRejectsUnsafeAddresses(string url)
    {
        Assert.Throws<ArgumentException>(
            () => new AgentWebSearchEntry(url, "Unsafe result"));
    }

    [Fact]
    public void SearchResultCopiesItsEntries()
    {
        List<AgentWebSearchEntry> entries =
        [
            new AgentWebSearchEntry("https://example.test", "Example"),
        ];
        var result = new AgentWebSearchResult(
            "https://www.google.com/search?q=example",
            "Search",
            entries,
            truncated: false);

        entries.Clear();

        Assert.Single(result.Entries);
    }
}
