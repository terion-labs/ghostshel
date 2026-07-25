using System.Text;
using System.Text.Json;
using GhostShell.Application;

namespace GhostShell.Browser.Tests;

public sealed class AvaloniaNativeBrowserViewSnapshotTests
{
    private const string SnapshotNonce = "snapshot_test";

    private const string ValidPayload = """
        {
          "snapshot_nonce": "snapshot_test",
          "mutation_epoch": 7,
          "nodes": [
            {
              "depth": 0,
              "role": "document",
              "name": "Example",
              "states": 0,
              "handle": null
            },
            {
              "depth": 1,
              "role": "button",
              "name": "Continue",
              "states": 33,
              "handle": "e1"
            }
          ],
          "truncated": false
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsesDirectAndVendorEncodedSnapshotPayloads(
        bool encodeAsJsonString)
    {
        var raw = encodeAsJsonString
            ? JsonSerializer.Serialize(ValidPayload)
            : ValidPayload;

        var result = AvaloniaNativeBrowserView.ParseSnapshot(
            raw,
            SnapshotNonce);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value?.IsTruncated);
        var button = result.Value!.Nodes[1];
        Assert.Equal(1, button.Depth);
        Assert.Equal("button", button.Role);
        Assert.Equal("Continue", button.Name);
        Assert.Equal(
            BrowserSnapshotNodeState.Disabled
                | BrowserSnapshotNodeState.Required,
            button.States);
        Assert.Equal(SnapshotNonce, button.Handle?.SnapshotNonce);
        Assert.Equal("e1", button.Handle?.ElementToken);
        Assert.Equal(7, button.Handle?.MutationEpoch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"snapshot_nonce\":\"snapshot_test\",\"mutation_epoch\":0,\"nodes\":[],\"truncated\":false}")]
    [InlineData("{\"snapshot_nonce\":\"wrong\",\"mutation_epoch\":0,\"nodes\":[{\"depth\":0,\"role\":\"document\",\"name\":\"x\",\"states\":0,\"handle\":null}],\"truncated\":false}")]
    [InlineData("{\"snapshot_nonce\":\"snapshot_test\",\"mutation_epoch\":-1,\"nodes\":[{\"depth\":0,\"role\":\"document\",\"name\":\"x\",\"states\":0,\"handle\":null}],\"truncated\":false}")]
    [InlineData("{\"snapshot_nonce\":\"snapshot_test\",\"mutation_epoch\":0,\"nodes\":[{\"depth\":0,\"role\":\"DOCUMENT\",\"name\":\"x\",\"states\":0,\"handle\":null}],\"truncated\":false}")]
    [InlineData("{\"snapshot_nonce\":\"snapshot_test\",\"mutation_epoch\":0,\"nodes\":[{\"depth\":0,\"role\":\"document\",\"name\":\"x\",\"states\":0,\"handle\":\"e0\"}],\"truncated\":false}")]
    public void RejectsMalformedOrUntrustedNativePayloads(string raw)
    {
        var result = AvaloniaNativeBrowserView.ParseSnapshot(
            raw,
            SnapshotNonce);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            NativeBrowserSnapshotFailure.Invalid,
            result.Failure);
    }

    [Fact]
    public void RejectsOversizedNativePayloadBeforeJsonProjection()
    {
        var result = AvaloniaNativeBrowserView.ParseSnapshot(
            new string('x', 256 * 1024 + 1),
            SnapshotNonce);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            NativeBrowserSnapshotFailure.Invalid,
            result.Failure);
    }

    [Fact]
    public void SnapshotScriptBoundsTraversalAndAvoidsUnboundedDomProjection()
    {
        var script = AvaloniaNativeBrowserView.SnapshotScriptForTests;

        Assert.Contains("const maximumVisitedElements = 4096;", script);
        Assert.Contains("const maximumTraversalDepth = 64;", script);
        Assert.Contains("let remainingTextNodes = 4096;", script);
        Assert.Contains("const maximumTextNodesPerLabel = 64;", script);
        Assert.Contains("const maximumDirectTextNodes = 32;", script);
        Assert.Contains("const utf8Width = codePoint =>", script);
        Assert.Contains(
            "state.bytes + requiredBytes > state.maximumBytes",
            script);
        Assert.DoesNotContain("Array.from", script);
        Assert.DoesNotContain(".textContent", script);
        Assert.DoesNotContain(".closest(", script);
        Assert.DoesNotContain("parent.children", script);
        Assert.DoesNotContain("createTreeWalker", script);
        Assert.DoesNotContain("JSON.stringify", script);
        Assert.DoesNotContain("eval(", script);
        Assert.DoesNotContain("new Function", script);
        Assert.Contains(
            "const serializeSnapshot = (",
            script);
        Assert.Contains("new MutationObserver", script);
        Assert.Contains("entries.set(token, { element, validate });", script);
        var nativeActivation = script.IndexOf(
            "nativeClick.call(entry.element);",
            StringComparison.Ordinal);
        var finalMutationFlush = script.LastIndexOf(
            "flushMutations();",
            nativeActivation,
            StringComparison.Ordinal);
        Assert.True(
            finalMutationFlush >= 0
            && nativeActivation > finalMutationFlush);
        Assert.DoesNotContain("parentLocator", script);
        Assert.DoesNotContain("elementLocator", script);
    }

    [Theory]
    [MemberData(nameof(MaximumUtf8Names))]
    public void ParserAcceptsLabelsClippedToMaximumUtf8Bytes(string name)
    {
        var result = AvaloniaNativeBrowserView.ParseSnapshot(
            PayloadWithName(name),
            SnapshotNonce);

        Assert.Equal(256, Encoding.UTF8.GetByteCount(name));
        Assert.True(result.IsSuccess);
        Assert.Equal(name, Assert.Single(result.Value!.Nodes).Name);
    }

    [Theory]
    [MemberData(nameof(OversizedUtf8Names))]
    public void ParserRejectsLabelsBeyondMaximumUtf8Bytes(string name)
    {
        var result = AvaloniaNativeBrowserView.ParseSnapshot(
            PayloadWithName(name),
            SnapshotNonce);

        Assert.True(Encoding.UTF8.GetByteCount(name) > 256);
        Assert.False(result.IsSuccess);
        Assert.Equal(
            NativeBrowserSnapshotFailure.Invalid,
            result.Failure);
    }

    public static TheoryData<string> MaximumUtf8Names => new()
    {
        new string('\u0416', 128),
        string.Concat(Enumerable.Repeat("\U0001F600", 64)),
        string.Concat(new string('\u0416', 126), "\U0001F600"),
    };

    public static TheoryData<string> OversizedUtf8Names => new()
    {
        new string('\u0416', 129),
        string.Concat(Enumerable.Repeat("\U0001F600", 65)),
        string.Concat(new string('\u0416', 127), "\U0001F600"),
    };

    private static string PayloadWithName(string name) =>
        JsonSerializer.Serialize(
            new
            {
                snapshot_nonce = SnapshotNonce,
                mutation_epoch = 0,
                nodes = new[]
                {
                    new
                    {
                        depth = 0,
                        role = "document",
                        name,
                        states = 0,
                        handle = (string?)null,
                    },
                },
                truncated = false,
            });
}
