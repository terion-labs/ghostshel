using System.Text.Json;
using Exclr8Cef.Cdp;

namespace Exclr8Cef.WebView.Tests;

public sealed class AccessibilityClientTests
{
    [Theory]
    [InlineData(
        "{\"value\":{\"type\":\"tristate\",\"value\":\"true\"}}",
        "true")]
    [InlineData(
        "{\"type\":\"boolean\",\"value\":false}",
        "false")]
    [InlineData(
        "{\"type\":\"string\",\"value\":\"mixed\"}",
        "mixed")]
    public void AxValueEnvelopesAreUnwrappedToTheirSemanticValue(
        string json,
        string expected)
    {
        using var document = JsonDocument.Parse(json);

        var actual = AccessibilityClient.ReadAxValue(document.RootElement);

        Assert.Equal(expected, actual);
    }
}
