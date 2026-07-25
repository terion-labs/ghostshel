using System.Text.Json;

namespace GhostShell.Browser.Tests;

public sealed class AvaloniaNativeBrowserViewCheckTests
{
    [Theory]
    [InlineData("checked", 0)]
    [InlineData("stale", 1)]
    [InlineData("not_interactable", 2)]
    [InlineData("not_checkable", 3)]
    [InlineData("outcome_unknown", 4)]
    public void ParsesOnlyClosedDirectAndVendorEncodedResults(
        string status,
        int expectedValue)
    {
        var payload = string.Concat(
            "{\"status\":\"",
            status,
            "\"}");

        var direct = AvaloniaNativeBrowserView.ParseCheck(payload);
        var encoded = AvaloniaNativeBrowserView.ParseCheck(
            JsonSerializer.Serialize(payload));

        var expected = (NativeBrowserCheckStatus)expectedValue;
        Assert.Equal(expected, direct.Status);
        Assert.Equal(expected, encoded.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"checked\",\"extra\":true}")]
    [InlineData("{\"status\":\"checked\",\"status\":\"stale\"}")]
    [InlineData("{\"status\":\"other\"}")]
    [InlineData("\"\\\"checked\\\"\"")]
    public void MalformedResultsAreOutcomeUnknown(string raw)
    {
        var result = AvaloniaNativeBrowserView.ParseCheck(raw);

        Assert.Equal(
            NativeBrowserCheckStatus.OutcomeUnknown,
            result.Status);
    }

    [Fact]
    public void OversizedResultsAreOutcomeUnknown()
    {
        var result = AvaloniaNativeBrowserView.ParseCheck(
            new string('x', 1_025));

        Assert.Equal(
            NativeBrowserCheckStatus.OutcomeUnknown,
            result.Status);
    }

    [Fact]
    public void FixedCheckScriptUsesOnlyPrivateRegistryHandles()
    {
        var script = AvaloniaNativeBrowserView.CheckScriptForTests;

        Assert.Contains("registry.check(", script);
        Assert.Contains("\"ghostshell_snapshot_test\"", script);
        Assert.Contains("\"ghostshell_element_test\"", script);
        Assert.DoesNotContain("getOwnPropertyDescriptor", script);
        Assert.DoesNotContain("querySelector", script);
        Assert.DoesNotContain("eval(", script);
        Assert.DoesNotContain("new Function", script);
        Assert.DoesNotContain("JSON.stringify", script);
        Assert.DoesNotContain("__GHOSTSHELL_", script);
    }

    [Fact]
    public void SnapshotRegistryPinsTheExactCheckContract()
    {
        var script = AvaloniaNativeBrowserView.SnapshotScriptForTests;
        var checkMethod = script.IndexOf(
            "check(secret, nonce, token, expectedEpoch)",
            StringComparison.Ordinal);
        var entriesConsumed = script.IndexOf(
            "entries.clear();",
            checkMethod,
            StringComparison.Ordinal);
        var exactInput = script.IndexOf(
            "element instanceof HTMLInputElement",
            checkMethod,
            StringComparison.Ordinal);
        var typeRead = script.IndexOf(
            "nativeInputTypeGetter.call(element)",
            checkMethod,
            StringComparison.Ordinal);
        var initialCheckedRead = script.IndexOf(
            "checked = nativeInputCheckedGetter.call(element);",
            checkMethod,
            StringComparison.Ordinal);
        var alreadyChecked = script.IndexOf(
            "if (checked === true) return status(\"checked\");",
            checkMethod,
            StringComparison.Ordinal);
        var nativeClick = script.IndexOf(
            "nativeClick.call(element);",
            checkMethod,
            StringComparison.Ordinal);
        var verifiedChecked = script.IndexOf(
            "nativeInputCheckedGetter.call(element) === true",
            nativeClick,
            StringComparison.Ordinal);

        Assert.True(
            checkMethod >= 0
            && entriesConsumed > checkMethod
            && exactInput > entriesConsumed
            && typeRead > exactInput
            && initialCheckedRead > typeRead
            && alreadyChecked > initialCheckedRead
            && nativeClick > alreadyChecked
            && verifiedChecked > nativeClick);
        Assert.Contains(
            "inputType !== \"checkbox\"",
            script);
        Assert.Contains(
            "inputType !== \"radio\"",
            script);
        Assert.Contains(
            "typeof nativeInputCheckedGetter !== \"function\"",
            script);
        Assert.Contains(
            "const nativeInputCheckedGetter =",
            script);
        Assert.DoesNotContain(
            "element.checked =",
            script);
        Assert.DoesNotContain(
            "nativeDispatchEvent",
            script[checkMethod..]);
    }
}
