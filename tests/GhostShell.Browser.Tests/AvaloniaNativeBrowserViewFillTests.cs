using System.Text.Json;

namespace GhostShell.Browser.Tests;

public sealed class AvaloniaNativeBrowserViewFillTests
{
    [Theory]
    [InlineData("filled", 0)]
    [InlineData("stale", 1)]
    [InlineData(
        "not_interactable",
        2)]
    [InlineData(
        "not_fillable",
        3)]
    [InlineData(
        "outcome_unknown",
        4)]
    [InlineData(
        "value_not_supported",
        5)]
    public void ParsesOnlyClosedDirectAndVendorEncodedResults(
        string status,
        int expectedValue)
    {
        var payload = string.Concat(
            "{\"status\":\"",
            status,
            "\"}");

        var direct = AvaloniaNativeBrowserView.ParseFill(payload);
        var encoded = AvaloniaNativeBrowserView.ParseFill(
            JsonSerializer.Serialize(payload));

        var expected = (NativeBrowserFillStatus)expectedValue;
        Assert.Equal(expected, direct.Status);
        Assert.Equal(expected, encoded.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"filled\",\"extra\":true}")]
    [InlineData("{\"status\":\"filled\",\"status\":\"stale\"}")]
    [InlineData("{\"status\":\"other\"}")]
    [InlineData("\"\\\"filled\\\"\"")]
    public void MalformedResultsAreOutcomeUnknown(string raw)
    {
        var result = AvaloniaNativeBrowserView.ParseFill(raw);

        Assert.Equal(
            NativeBrowserFillStatus.OutcomeUnknown,
            result.Status);
    }

    [Fact]
    public void FixedFillScriptUsesOnlyPrivateHandlesAndJsonText()
    {
        const string adversarialText =
            "\"});globalThis.pwned=true;//\u2028</script>";

        var script =
            AvaloniaNativeBrowserView.FillScriptForTests(adversarialText);

        Assert.Contains("registry.fill(", script);
        Assert.Contains("\"ghostshell_snapshot_test\"", script);
        Assert.Contains("\"ghostshell_element_test\"", script);
        Assert.DoesNotContain(adversarialText, script);
        Assert.DoesNotContain("querySelector", script);
        Assert.DoesNotContain("eval(", script);
        Assert.DoesNotContain("new Function", script);
        Assert.DoesNotContain("JSON.stringify", script);
        Assert.DoesNotContain("__GHOSTSHELL_", script);
    }

    [Fact]
    public void SnapshotRegistryPinsTheExactFillContract()
    {
        var script = AvaloniaNativeBrowserView.SnapshotScriptForTests;

        var fillMethod = script.IndexOf(
            "fill(secret, nonce, token, expectedEpoch, text)",
            StringComparison.Ordinal);
        var entriesConsumed = script.IndexOf(
            "entries.clear();",
            fillMethod,
            StringComparison.Ordinal);
        var nativeSetter = script.IndexOf(
            "nativeValueSetter.call(element, text);",
            fillMethod,
            StringComparison.Ordinal);
        var unsupportedValue = script.IndexOf(
            "status(\"value_not_supported\")",
            fillMethod,
            StringComparison.Ordinal);
        var finalMutationFlush = script.LastIndexOf(
            "flushMutations();",
            nativeSetter,
            StringComparison.Ordinal);
        var eventDispatch = script.IndexOf(
            "nativeDispatchEvent.call(",
            nativeSetter,
            StringComparison.Ordinal);
        var postDispatchVerification = script.IndexOf(
            "nativeValueGetter.call(element) !== text",
            eventDispatch,
            StringComparison.Ordinal);
        Assert.True(
            fillMethod >= 0
            && entriesConsumed > fillMethod
            && unsupportedValue > entriesConsumed
            && finalMutationFlush > entriesConsumed
            && nativeSetter > finalMutationFlush
            && nativeSetter > unsupportedValue
            && eventDispatch > nativeSetter
            && postDispatchVerification > eventDispatch);
        Assert.Contains("const fillableInputTypes = new Set([", script);
        Assert.Contains(
            "\"text\", \"search\", \"email\", \"url\", \"tel\"",
            script);
        Assert.Contains(
            "element instanceof HTMLTextAreaElement",
            script);
        Assert.Contains(
            "element instanceof HTMLInputElement",
            script);
        Assert.Contains("element.readOnly", script);
        Assert.Contains("aria-readonly", script);
        Assert.Contains("const isEffectivelyDisabled = element =>", script);
        Assert.Contains(
            "ancestor.localName === \"fieldset\"",
            script);
        Assert.Contains(
            "nativeContains.call(firstLegend, element)",
            script);
        Assert.Contains(
            "nativeValueSetter.call(element, text);",
            script);
        Assert.Contains(
            "nativeValueGetter.call(element) !== text",
            script);
        Assert.Contains(
            "nativeInputTypeGetter.call(element)",
            script);
        Assert.Contains(
            "typeof nativeValueGetter !== \"function\"",
            script);
        Assert.Contains("containsLineBreak(text)", script);
        Assert.Contains("containsCarriageReturn(text)", script);
        Assert.Contains("multipleEmailWouldNormalize(text)", script);
        Assert.Contains("hasEdgeAsciiWhitespace(", script);
        Assert.Contains("new NativeEvent(\"input\", {", script);
        Assert.Contains("bubbles: true", script);
        Assert.Contains("composed: true", script);
        Assert.Contains(
            "if (expectedEpoch !== mutationEpoch)",
            script);
        Assert.DoesNotContain("contentEditable =", script);
    }
}
