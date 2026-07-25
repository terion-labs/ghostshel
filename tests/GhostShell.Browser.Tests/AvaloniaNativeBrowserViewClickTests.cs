using System.Text.Json;

namespace GhostShell.Browser.Tests;

public sealed class AvaloniaNativeBrowserViewClickTests
{
    [Theory]
    [InlineData("activated", 0)]
    [InlineData("stale", 1)]
    [InlineData(
        "not_interactable",
        2)]
    [InlineData(
        "outcome_unknown",
        3)]
    public void ParsesOnlyClosedDirectAndVendorEncodedResults(
        string status,
        int expectedValue)
    {
        var payload = string.Concat(
            "{\"status\":\"",
            status,
            "\"}");

        var direct = AvaloniaNativeBrowserView.ParseClick(payload);
        var encoded = AvaloniaNativeBrowserView.ParseClick(
            JsonSerializer.Serialize(payload));

        var expected = (NativeBrowserClickStatus)expectedValue;
        Assert.Equal(expected, direct.Status);
        Assert.Equal(expected, encoded.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"activated\",\"extra\":true}")]
    [InlineData("{\"status\":\"activated\",\"status\":\"stale\"}")]
    [InlineData("{\"status\":\"other\"}")]
    [InlineData("\"\\\"activated\\\"\"")]
    public void MalformedResultsAreOutcomeUnknown(string raw)
    {
        var result = AvaloniaNativeBrowserView.ParseClick(raw);

        Assert.Equal(
            NativeBrowserClickStatus.OutcomeUnknown,
            result.Status);
    }

    [Fact]
    public void FixedClickScriptUsesOnlyPrivateRegistryHandles()
    {
        var script = AvaloniaNativeBrowserView.ClickScriptForTests;

        Assert.Contains("registry.activate(", script);
        Assert.Contains("\"ghostshell_snapshot_test\"", script);
        Assert.Contains("\"ghostshell_element_test\"", script);
        Assert.DoesNotContain("getOwnPropertyDescriptor", script);
        Assert.DoesNotContain("querySelector", script);
        Assert.DoesNotContain("eval(", script);
        Assert.DoesNotContain("new Function", script);
        Assert.DoesNotContain("JSON.stringify", script);
        Assert.DoesNotContain("__GHOSTSHELL_", script);
    }
}
