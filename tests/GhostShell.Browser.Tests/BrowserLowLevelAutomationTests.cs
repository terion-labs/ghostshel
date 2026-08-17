using System.Text.Json;
using Avalonia;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Browser.Tests;

public sealed class BrowserLowLevelAutomationTests
{
    [Fact]
    public async Task AcknowledgedClickAdvancesInputEpochAndReturnsFreshState()
    {
        var native = new RecordingEmbeddedBrowserView();
        var surface = Surface(native);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        var request = new BrowserMouseRequest(
            new SessionId("browser"), binding, BrowserMouseAction.Click,
            20, 30, BrowserMouseButton.Left, clickCount: 1);

        var result = await surface.DispatchMouseWithinOriginAsync(
            request,
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(request, native.LastMouseRequest);
        Assert.Equal(binding.InputEpoch + 1, result.Value!.FreshState.InputEpoch);
        Assert.Equal(result.Value.FreshState, surface.State);
    }

    [Fact]
    public async Task NativeViewportChangeFailsBeforeInputDispatch()
    {
        var native = new RecordingEmbeddedBrowserView();
        var surface = Surface(native);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        native.Viewport = new NativeBrowserViewport(700, 600);

        var result = await surface.DispatchMouseWithinOriginAsync(
            new BrowserMouseRequest(
                new SessionId("browser"), binding, BrowserMouseAction.Move, 20, 30),
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.NavigationStateChanged, result.Error?.Code);
        Assert.Null(native.LastMouseRequest);
        Assert.Equal(700, surface.State.Viewport.WidthCss);
        Assert.Equal(binding.ViewportRevision + 1, surface.State.ViewportRevision);
    }

    [Fact]
    public async Task CancellationAfterDispatchQuarantinesAndDoesNotReportCancelled()
    {
        var pending = new TaskCompletionSource<NativeBrowserAutomationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new RecordingEmbeddedBrowserView { PendingAutomation = pending };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = Surface(native, replacement);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        using var cancellation = new CancellationTokenSource();

        var action = surface.DispatchKeyWithinOriginAsync(
            new BrowserKeyRequest(
                new SessionId("browser"), binding,
                BrowserKeyAction.Press, BrowserKey.Enter),
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            cancellation.Token).AsTask();
        await WaitUntilAsync(() => native.LastKeyRequest is not null);
        cancellation.Cancel();
        var result = await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.InteractionOutcomeUnknown, result.Error?.Code);
        Assert.True(native.IsDisposed);
        Assert.NotEqual(binding.Document.DocumentRevision, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task ScriptNavigationOutsideFrozenOriginIsDeniedAndQuarantined()
    {
        var pending = new TaskCompletionSource<NativeBrowserAutomationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new RecordingEmbeddedBrowserView { PendingAutomation = pending };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = Surface(native, replacement);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);
        var origin = BrowserNavigationOrigin.FromAddress(surface.State.Address);

        var action = surface.EvaluateWithinOriginAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), binding, "location.href"),
            origin,
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() => native.LastEvaluateRequest is not null);
        Assert.True(native.RaiseNavigationStarted(Address("https://other.test/")));
        pending.SetResult(NativeBrowserAutomationResult.Acknowledged("null"));
        var result = await action.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.NavigationPolicyDenied, result.Error?.Code);
        Assert.True(native.IsDisposed);
    }

    [Fact]
    public async Task EvaluationRejectsSecretBearingResultAfterAcknowledgement()
    {
        var native = new RecordingEmbeddedBrowserView
        {
            EvaluationResult = NativeBrowserAutomationResult.Acknowledged(
                "{\"password\":\"page-controlled\"}"),
        };
        var surface = Surface(native);
        Arrange(surface);
        var binding = BrowserAutomationBinding.FromState(surface.State);

        var result = await surface.EvaluateWithinOriginAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), binding, "({value: 1})"),
            BrowserNavigationOrigin.FromAddress(surface.State.Address),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.ScriptResultRejected, result.Error?.Code);
    }

    [Fact]
    public async Task CdpClickIsAnAtomicAcknowledgedPressReleaseGesture()
    {
        var transport = new RecordingTransport(
            "{\"result\":{}}",
            "{\"result\":{}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.DispatchMouseAsync(
            new BrowserMouseRequest(
                new SessionId("browser"), Binding(), BrowserMouseAction.Click,
                20, 30, BrowserMouseButton.Left, clickCount: 1));

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        Assert.Equal(
            ["Input.dispatchMouseEvent", "Input.dispatchMouseEvent"],
            transport.Calls.Select(call => call.Method));
        Assert.Contains("mousePressed", transport.Calls[0].Parameters);
        Assert.Contains("mouseReleased", transport.Calls[1].Parameters);
    }

    [Fact]
    public async Task IsolatedEvaluationCreatesPrivateWorldAndForbidsSideEffects()
    {
        var transport = new RecordingTransport(
            "{\"result\":{\"frameTree\":{\"frame\":{\"id\":\"main\"}}}}",
            "{\"result\":{\"executionContextId\":42}}",
            "{\"result\":{\"result\":{\"type\":\"number\",\"value\":2}}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.EvaluateAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), Binding(), "1 + 1"));

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        Assert.Equal("2", result.ResultJson);
        Assert.Equal(
            ["Page.getFrameTree", "Page.createIsolatedWorld", "Runtime.evaluate"],
            transport.Calls.Select(call => call.Method));
        using var parameters = JsonDocument.Parse(transport.Calls[2].Parameters!);
        Assert.True(parameters.RootElement.GetProperty("throwOnSideEffect").GetBoolean());
        Assert.Equal(42, parameters.RootElement.GetProperty("contextId").GetInt32());
        Assert.True(parameters.RootElement.GetProperty("returnByValue").GetBoolean());
    }

    [Fact]
    public async Task MainWorldEvaluationOmitsContextIdInsteadOfSendingNull()
    {
        var transport = new RecordingTransport(
            "{\"result\":{\"result\":{\"type\":\"number\",\"value\":1}}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.EvaluateAsync(
            new BrowserEvaluateRequest(
                new SessionId("browser"), Binding(), "1",
                BrowserEvaluationWorld.Main));

        Assert.Equal(NativeBrowserAutomationStatus.Acknowledged, result.Status);
        using var parameters = JsonDocument.Parse(transport.Calls[0].Parameters!);
        Assert.False(parameters.RootElement.TryGetProperty("contextId", out _));
    }

    [Fact]
    public async Task OversizedCdpReplyFailsClosedAfterDispatch()
    {
        var transport = new RecordingTransport(
            "{\"result\":{\"padding\":\"" + new string('x', 300_000) + "\"}}");
        var adapter = new CefBrowserAutomationAdapter(transport);

        var result = await adapter.DispatchKeyAsync(
            new BrowserKeyRequest(
                new SessionId("browser"), Binding(),
                BrowserKeyAction.Press, BrowserKey.Enter));

        Assert.Equal(NativeBrowserAutomationStatus.OutcomeUnknown, result.Status);
    }

    private static BrowserSurface Surface(
        RecordingEmbeddedBrowserView native,
        RecordingEmbeddedBrowserView? replacement = null) =>
        replacement is null
            ? new BrowserSurface(
                native,
                InlineBrowserUiDispatcher.Instance,
                capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate)
            : new BrowserSurface(
                native,
                InlineBrowserUiDispatcher.Instance,
                () => replacement,
                static _ => { },
                capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);

    private static void Arrange(BrowserSurface surface)
    {
        surface.Measure(new Size(800, 600));
        surface.Arrange(new Rect(0, 0, 800, 600));
        Assert.Equal(new BrowserViewportState(800, 600, 1), surface.State.Viewport);
    }

    private static BrowserAutomationBinding Binding() =>
        new(
            new BrowserDocumentBinding(BrowserAddress.Blank, 0),
            new BrowserViewportState(800, 600, 1),
            1,
            0);

    private static BrowserAddress Address(string value)
    {
        Assert.True(BrowserAddress.TryParse(value, out var address));
        return address;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }

    private sealed class RecordingTransport(params string[] replies)
        : ICefDevToolsTransport
    {
        private readonly Queue<string> _replies = new(replies);

        public List<(string Method, string? Parameters)> Calls { get; } = [];

        public Task<string> ExecuteAsync(string method, string? parametersJson)
        {
            Calls.Add((method, parametersJson));
            return Task.FromResult(_replies.Dequeue());
        }
    }
}
