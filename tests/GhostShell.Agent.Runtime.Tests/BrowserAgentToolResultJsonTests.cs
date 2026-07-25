using System.Text;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime.Tests;

public sealed class BrowserAgentToolResultJsonTests
{
    [Fact]
    public void StateResultIsBoundedLabeledAndRemovesAddressSecrets()
    {
        var state = State(
            "https://example.test/operations?token=secret-canary#private",
            "password=secret-canary",
            documentRevision: 12,
            canGoBack: true);

        var json = BrowserAgentToolResultJson.Success(
            new AgentBrowserActionResult.State(state),
            new PanelInstanceId("panel-browser"));

        Assert.DoesNotContain("secret-canary", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "panel-browser",
            root.GetProperty("panel_id").GetString());
        Assert.Equal(
            "untrusted_browser",
            root.GetProperty("content_origin").GetString());
        Assert.Equal(
            "https://example.test/operations",
            root.GetProperty("address").GetString());
        Assert.False(root.GetProperty("address_truncated").GetBoolean());
        Assert.Equal(
            "[REDACTED SECRET-BEARING LINE]",
            root.GetProperty("title").GetString());
        Assert.Equal(1, root.GetProperty("title_redactions").GetInt32());
        Assert.False(root.GetProperty("title_truncated").GetBoolean());
        Assert.Equal("ready", root.GetProperty("load_state").GetString());
        Assert.True(root.GetProperty("can_go_back").GetBoolean());
        Assert.False(root.GetProperty("can_go_forward").GetBoolean());
        Assert.Equal(12, root.GetProperty("document_revision").GetInt64());
    }

    [Fact]
    public void ExpandedRedactionsCannotMakeTheReturnedTitleUnbounded()
    {
        var title = string.Join(
            '\n',
            Enumerable.Repeat("token=", 146));
        Assert.True(title.Length <= BrowserSessionState.MaximumTitleLength);
        var state = State("about:blank", title, documentRevision: 1);

        var json = BrowserAgentToolResultJson.Success(
            new AgentBrowserActionResult.State(state));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var returnedTitle = root.GetProperty("title").GetString()!;
        Assert.Equal("about:blank", root.GetProperty("address").GetString());
        Assert.True(root.GetProperty("title_truncated").GetBoolean());
        Assert.Equal(146, root.GetProperty("title_redactions").GetInt32());
        Assert.True(Encoding.UTF8.GetByteCount(returnedTitle) <= 4 * 1024);
        Assert.DoesNotContain('\uFFFD', returnedTitle);
    }

    [Fact]
    public void FailedPageStateExposesStableFailureButNeverEngineMessage()
    {
        var failure = BrowserError.Create(
            BrowserErrorCode.NavigationFailed,
            "page-controlled secret-canary",
            retryable: true);
        var state = new BrowserSessionState(
            new BrowserAddress(new Uri("https://example.test/failure")),
            "Load failed",
            BrowserLoadState.Failed,
            canGoBack: false,
            canGoForward: true,
            documentRevision: 7,
            failure);

        var json = BrowserAgentToolResultJson.Success(
            new AgentBrowserActionResult.State(state));

        Assert.DoesNotContain("secret-canary", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var projectedFailure = root.GetProperty("failure");
        Assert.Equal("failed", root.GetProperty("load_state").GetString());
        Assert.Equal(
            "navigation_failed",
            projectedFailure.GetProperty("code").GetString());
        Assert.True(projectedFailure.GetProperty("retryable").GetBoolean());
        Assert.False(projectedFailure.TryGetProperty("message", out _));
    }

    [Fact]
    public void MutationSuccessContainsNoBrowserContent()
    {
        var json = BrowserAgentToolResultJson.Success(
            new AgentBrowserActionResult.Completed());

        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void SnapshotResultIsStructuredLabeledAndRemovesPageSecrets()
    {
        var capturedAt = new DateTimeOffset(
            2026,
            7,
            24,
            8,
            30,
            0,
            TimeSpan.Zero);
        var documentBinding = new BrowserDocumentBinding(
            new BrowserAddress(
                new Uri(
                    "https://example.test/operations?token=query-secret#private")),
            documentRevision: 19);
        var snapshot = new BrowserDocumentSnapshot(
            documentBinding,
            [
                new BrowserSnapshotNode(
                    depth: 0,
                    role: "document",
                    name: "Operations"),
                new BrowserSnapshotNode(
                    depth: 1,
                    role: "button",
                    name: "password=node-secret",
                    reference: new BrowserElementReference(
                        "snapshot-node-1",
                        documentBinding),
                    states:
                        BrowserSnapshotNodeState.Disabled
                        | BrowserSnapshotNodeState.Required),
            ],
            capturedAt,
            isTruncated: true);

        var json = BrowserAgentToolResultJson.Success(
            new AgentBrowserActionResult.Snapshot(snapshot),
            new PanelInstanceId("panel-browser"));

        Assert.DoesNotContain("query-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("node-secret", json, StringComparison.Ordinal);
        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "panel-browser",
            root.GetProperty("panel_id").GetString());
        Assert.Equal(
            "untrusted_browser",
            root.GetProperty("content_origin").GetString());
        Assert.Equal(
            "https://example.test/operations",
            root.GetProperty("address").GetString());
        Assert.False(root.GetProperty("address_truncated").GetBoolean());
        Assert.Equal(
            documentBinding.DocumentRevision,
            root.GetProperty("document_revision").GetInt64());
        Assert.Equal(
            capturedAt,
            root.GetProperty("captured_at_utc").GetDateTimeOffset());
        Assert.True(root.GetProperty("is_truncated").GetBoolean());
        Assert.Equal(1, root.GetProperty("redactions").GetInt32());

        var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.Equal(0, nodes[0].GetProperty("depth").GetInt32());
        Assert.Equal("document", nodes[0].GetProperty("role").GetString());
        Assert.Empty(nodes[0].GetProperty("states").EnumerateArray());
        Assert.False(nodes[0].TryGetProperty("reference", out _));
        Assert.Equal(
            "[REDACTED SECRET-BEARING LINE]",
            nodes[1].GetProperty("name").GetString());
        Assert.Equal(
            "snapshot-node-1",
            nodes[1].GetProperty("reference").GetString());
        Assert.Equal(
            ["disabled", "required"],
            nodes[1]
                .GetProperty("states")
                .EnumerateArray()
                .Select(value => value.GetString()));
    }

    [Fact]
    public void SnapshotRedactionExpansionRemainsWithinNodeBounds()
    {
        var name = string.Join(
            '\n',
            Enumerable.Repeat("token=", 36));
        Assert.True(
            Encoding.UTF8.GetByteCount(name)
            <= BrowserSnapshotNode.MaximumNameBytes);
        var documentBinding = new BrowserDocumentBinding(
            BrowserAddress.Blank,
            documentRevision: 1);
        var snapshot = new BrowserDocumentSnapshot(
            documentBinding,
            [new BrowserSnapshotNode(0, "document", name)],
            DateTimeOffset.UnixEpoch);

        var json = BrowserAgentToolResultJson.Success(
            new AgentBrowserActionResult.Snapshot(snapshot));

        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;
        var projectedName = Assert.Single(
                root.GetProperty("nodes").EnumerateArray())
            .GetProperty("name")
            .GetString()!;
        Assert.True(root.GetProperty("is_truncated").GetBoolean());
        Assert.Equal(36, root.GetProperty("redactions").GetInt32());
        Assert.True(
            Encoding.UTF8.GetByteCount(projectedName)
            <= BrowserSnapshotNode.MaximumNameBytes);
        Assert.DoesNotContain('\uFFFD', projectedName);
    }

    [Fact]
    public void WorstCaseSnapshotProjectionStaysInsideKernelResultLimit()
    {
        var addressPrefix = "https://example.test/";
        var document = new BrowserDocumentBinding(
            new BrowserAddress(
                new Uri(
                    string.Concat(
                        addressPrefix,
                        new string(
                            '&',
                            BrowserAddress.MaximumLength
                                - addressPrefix.Length)))),
            documentRevision: 1);
        var nodes = Enumerable.Range(
                0,
                BrowserDocumentSnapshot.MaximumNodeCount)
            .Select(index =>
            {
                var prefix = string.Concat("ref", index, "_");
                var reference = new BrowserElementReference(
                    string.Concat(
                        prefix,
                        new string(
                            'a',
                            BrowserElementReference.MaximumValueBytes
                                - prefix.Length)),
                    document);
                return new BrowserSnapshotNode(
                    0,
                    new string('r', BrowserSnapshotNode.MaximumRoleBytes),
                    new string('\u0001', BrowserSnapshotNode.MaximumNameBytes),
                    reference,
                    BrowserSnapshotNodeState.Disabled
                        | BrowserSnapshotNodeState.Checked
                        | BrowserSnapshotNodeState.Selected
                        | BrowserSnapshotNodeState.Expanded
                        | BrowserSnapshotNodeState.Pressed
                        | BrowserSnapshotNodeState.Required
                        | BrowserSnapshotNodeState.ReadOnly);
            })
            .ToArray();
        var snapshot = new BrowserDocumentSnapshot(
            document,
            nodes,
            DateTimeOffset.UnixEpoch);

        var json = BrowserAgentToolResultJson.Success(
            new AgentBrowserActionResult.Snapshot(snapshot),
            new PanelInstanceId(
                new string(
                    '&',
                    AgentToolResultJson.MaximumPanelIdBytes)));

        Assert.True(
            Encoding.UTF8.GetByteCount(json)
            <= AgentKernelLimits.Default.MaximumToolResultBytes);
        using var result = JsonDocument.Parse(json);
        Assert.True(
            result.RootElement
                .GetProperty("address_truncated")
                .GetBoolean());
        Assert.True(
            result.RootElement
                .GetProperty("is_truncated")
                .GetBoolean());
        Assert.True(
            result.RootElement
                .GetProperty("nodes")
                .GetArrayLength()
            < BrowserAgentToolResultJson.MaximumProviderSnapshotNodes);
    }

    [Fact]
    public void ProviderFacingPanelIdentifiersAreBounded()
    {
        var oversized = new PanelInstanceId(
            new string(
                'p',
                AgentToolResultJson.MaximumPanelIdBytes + 1));

        var exception = Assert.Throws<ArgumentException>(
            () => BrowserAgentToolResultJson.Success(
                new AgentBrowserActionResult.Completed(),
                oversized));

        Assert.Equal("panelId", exception.ParamName);
    }

    [Fact]
    public void UnknownHostStableCodesNeverCrossProviderBoundary()
    {
        var error = new HostError(
            HostErrorCode.EngineFailed,
            "password_super-secret-canary",
            "host secret-canary",
            Retryable: true);

        var json = BrowserAgentToolResultJson.Failure(error);

        Assert.Equal(
            "engine_failed",
            BrowserAgentToolResultJson.ProviderStableCode(error));
        Assert.DoesNotContain(
            "secret-canary",
            json,
            StringComparison.Ordinal);
        using var result = JsonDocument.Parse(json);
        Assert.Equal(
            "engine_failed",
            result.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    [Theory]
    [InlineData("browser_element_reference_stale")]
    [InlineData("browser_element_not_interactable")]
    [InlineData("browser_element_not_fillable")]
    [InlineData("browser_element_not_checkable")]
    [InlineData("browser_fill_value_not_supported")]
    [InlineData("browser_interaction_outcome_unknown")]
    public void ClosedInteractionStableCodesCrossTheProviderBoundary(
        string stableCode)
    {
        var error = new HostError(
            HostErrorCode.EngineFailed,
            stableCode,
            "host-only click detail",
            Retryable: false);

        var json = BrowserAgentToolResultJson.Failure(error);

        Assert.Equal(
            stableCode,
            BrowserAgentToolResultJson.ProviderStableCode(error));
        using var result = JsonDocument.Parse(json);
        var projected = result.RootElement.GetProperty("error");
        Assert.Equal(
            stableCode,
            projected.GetProperty("code").GetString());
        Assert.False(projected.GetProperty("retryable").GetBoolean());
        Assert.False(projected.TryGetProperty("message", out _));
    }

    [Fact]
    public void BrowserFailureUsesTheSharedSecretFreeEnvelope()
    {
        var json = BrowserAgentToolResultJson.Failure(
            BrowserError.Create(
                BrowserErrorCode.EngineFailed,
                "browser secret-canary",
                retryable: true),
            new PanelInstanceId("panel-browser"));

        Assert.DoesNotContain("secret-canary", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var error = root.GetProperty("error");
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "panel-browser",
            root.GetProperty("panel_id").GetString());
        Assert.Equal("engine_failed", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("message", out _));
    }

    [Fact]
    public void HostFailureAndRuntimeRejectionUseTheSameEnvelope()
    {
        var hostFailure = BrowserAgentToolResultJson.Failure(
            new HostError(
                HostErrorCode.InvalidRequest,
                "invalid_request",
                "host secret-canary",
                Retryable: false));
        var rejection = BrowserAgentToolResultJson.Rejected(
            "invalid_tool_arguments");

        Assert.DoesNotContain(
            "secret-canary",
            hostFailure,
            StringComparison.Ordinal);
        using var hostDocument = JsonDocument.Parse(hostFailure);
        using var rejectedDocument = JsonDocument.Parse(rejection);
        Assert.Equal(
            "invalid_request",
            hostDocument.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(
            "invalid_tool_arguments",
            rejectedDocument.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.False(
            rejectedDocument.RootElement
                .GetProperty("error")
                .GetProperty("retryable")
                .GetBoolean());
    }

    private static BrowserSessionState State(
        string address,
        string title,
        long documentRevision,
        bool canGoBack = false) =>
        new(
            new BrowserAddress(new Uri(address)),
            title,
            BrowserLoadState.Ready,
            canGoBack,
            canGoForward: false,
            documentRevision);
}
