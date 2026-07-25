using System.Text;
using System.Text.Json;
using GhostShell.Agent.Runtime;
using GhostShell.Application;

namespace GhostShell.Agent.Runtime.Tests;

public sealed class TerminalAgentToolResultJsonTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScreenResultIsBoundedLabeledAndRedactsObviousSecrets()
    {
        var longText = string.Concat(
            "password=hunter2\n",
            "safe output\n",
            "ghp_0123456789abcdef\n",
            string.Concat(Enumerable.Repeat("界", 20_000)));
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Screen(
                Screen(longText, contentRevision: 12)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = root.GetProperty("text").GetString()!;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "untrusted_terminal",
            root.GetProperty("content_origin").GetString());
        Assert.Equal(12, root.GetProperty("content_revision").GetInt64());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("redactions").GetInt32());
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 32 * 1024);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public void MutationSuccessContainsNoTerminalContent()
    {
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Completed());

        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScreenResultReportsExactMouseTrackingState(bool enabled)
    {
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Screen(
                Screen(
                    "menu",
                    contentRevision: 4,
                    mouseTrackingEnabled: enabled)));

        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            enabled,
            document.RootElement
                .GetProperty("mouse_tracking_enabled")
                .GetBoolean());
    }

    [Fact]
    public void HostFailureExposesStableFieldsButNotMessage()
    {
        var json = TerminalAgentToolResultJson.Failure(
            new HostError(
                HostErrorCode.EngineFailed,
                "engine_failed",
                "secret-canary",
                Retryable: true));

        Assert.DoesNotContain("secret-canary", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("engine_failed", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public void WaitResultCarriesOnlyBoundedScreenAndStableOutcome()
    {
        var snapshot = Screen("Selected", contentRevision: 8);
        var json = TerminalAgentToolResultJson.Success(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.Matched(snapshot, initialContentRevision: 7)));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("matched", root.GetProperty("wait_outcome").GetString());
        Assert.Equal(7, root.GetProperty("initial_content_revision").GetInt64());
        Assert.Equal("Selected", root.GetProperty("text").GetString());
    }

    private static TerminalScreenSnapshot Screen(
        string text,
        long contentRevision,
        bool mouseTrackingEnabled = false) =>
        new(
            text,
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 24,
            Columns: 80,
            IsAlternateScreen: true,
            WorkingDirectory: "/srv/private",
            CapturedAtUtc: Now,
            IsMouseTrackingEnabled: mouseTrackingEnabled,
            ContentRevision: contentRevision,
            WindowTitle: "secret host");
}
