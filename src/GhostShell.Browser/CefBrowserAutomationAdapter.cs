using System.Text.Json;
using System.Text;
using Exclr8Cef;
using GhostShell.Application;

namespace GhostShell.Browser;

internal interface ICefDevToolsTransport
{
    Task<string> ExecuteAsync(string method, string? parametersJson);
}

internal sealed class CefDevToolsTransport(CefBrowser browser)
    : ICefDevToolsTransport
{
    private readonly CefBrowser _browser = browser
        ?? throw new ArgumentNullException(nameof(browser));

    public Task<string> ExecuteAsync(string method, string? parametersJson) =>
        _browser.ExecuteDevToolsMethodAsync(method, parametersJson);
}

/// <summary>
/// Private typed CDP adapter. No method name, target identifier, execution
/// context, or remote-object handle crosses the Browser assembly boundary.
/// </summary>
internal sealed class CefBrowserAutomationAdapter(ICefDevToolsTransport transport)
{
    private const string IsolatedWorldName = "ghostshell-agent-isolated";
    private const int MaximumCdpReplyBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly ICefDevToolsTransport _transport = transport
        ?? throw new ArgumentNullException(nameof(transport));

    public async Task<NativeBrowserViewport> ReadViewportAsync()
    {
        using var reply = await ExecuteAsync("Page.getLayoutMetrics", parameters: null)
            .ConfigureAwait(false);
        var result = RequireResult(reply.RootElement);
        var viewport = result.TryGetProperty("cssVisualViewport", out var visual)
            ? visual
            : result.GetProperty("cssLayoutViewport");
        return new NativeBrowserViewport(
            viewport.GetProperty("clientWidth").GetDouble(),
            viewport.GetProperty("clientHeight").GetDouble()).Validate();
    }

    public Task<NativeBrowserAutomationResult> DispatchMouseAsync(
        BrowserMouseRequest request) =>
        CaptureOutcomeAsync(() => DispatchMouseCoreAsync(request));

    public Task<NativeBrowserAutomationResult> DispatchKeyAsync(
        BrowserKeyRequest request) =>
        CaptureOutcomeAsync(() => DispatchKeyCoreAsync(request));

    public Task<NativeBrowserAutomationResult> DispatchScrollAsync(
        BrowserScrollRequest request) =>
        CaptureOutcomeAsync(async () =>
        {
            await DispatchMouseEventAsync(
                    "mouseWheel",
                    request.OriginXCss,
                    request.OriginYCss,
                    BrowserMouseButton.None,
                    BrowserMouseButtons.None,
                    request.Modifiers,
                    clickCount: 0,
                    request.DeltaX,
                    request.DeltaY)
                .ConfigureAwait(false);
            return NativeBrowserAutomationResult.Acknowledged();
        });

    public Task<NativeBrowserAutomationResult> EvaluateAsync(
        BrowserEvaluateRequest request) =>
        CaptureOutcomeAsync(() => EvaluateCoreAsync(request));

    private async Task<NativeBrowserAutomationResult> DispatchMouseCoreAsync(
        BrowserMouseRequest request)
    {
        switch (request.Action)
        {
            case BrowserMouseAction.Move:
                await DispatchMouseEventAsync(
                        "mouseMoved", request.XCss, request.YCss,
                        BrowserMouseButton.None, request.Buttons,
                        request.Modifiers, 0, 0, 0)
                    .ConfigureAwait(false);
                break;
            case BrowserMouseAction.Click:
                await DispatchMouseEventAsync(
                        "mousePressed", request.XCss, request.YCss,
                        request.Button,
                        request.Buttons | ButtonFlag(request.Button),
                        request.Modifiers, request.ClickCount, 0, 0)
                    .ConfigureAwait(false);
                await DispatchMouseEventAsync(
                        "mouseReleased", request.XCss, request.YCss,
                        request.Button,
                        request.Buttons & ~ButtonFlag(request.Button),
                        request.Modifiers, request.ClickCount, 0, 0)
                    .ConfigureAwait(false);
                break;
            case BrowserMouseAction.Wheel:
                await DispatchMouseEventAsync(
                        "mouseWheel", request.XCss, request.YCss,
                        BrowserMouseButton.None, request.Buttons,
                        request.Modifiers, 0, request.DeltaX, request.DeltaY)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        return NativeBrowserAutomationResult.Acknowledged();
    }

    private async Task<NativeBrowserAutomationResult> DispatchKeyCoreAsync(
        BrowserKeyRequest request)
    {
        var key = KeyDescriptor.For(request.Key, request.Modifiers);
        await DispatchKeyEventAsync("keyDown", key, request.Modifiers)
            .ConfigureAwait(false);
        await DispatchKeyEventAsync("keyUp", key, request.Modifiers)
            .ConfigureAwait(false);

        return NativeBrowserAutomationResult.Acknowledged();
    }

    private async Task<NativeBrowserAutomationResult> EvaluateCoreAsync(
        BrowserEvaluateRequest request)
    {
        int? contextId = null;
        if (request.World == BrowserEvaluationWorld.Isolated)
        {
            using var frameReply = await ExecuteAsync("Page.getFrameTree", null)
                .ConfigureAwait(false);
            var frameId = RequireResult(frameReply.RootElement)
                .GetProperty("frameTree")
                .GetProperty("frame")
                .GetProperty("id")
                .GetString();
            if (string.IsNullOrWhiteSpace(frameId))
            {
                return NativeBrowserAutomationResult.Rejected("script_context_unavailable");
            }

            using var worldReply = await ExecuteAsync(
                    "Page.createIsolatedWorld",
                    new
                    {
                        frameId,
                        worldName = IsolatedWorldName,
                        grantUniveralAccess = false,
                    })
                .ConfigureAwait(false);
            contextId = RequireResult(worldReply.RootElement)
                .GetProperty("executionContextId")
                .GetInt32();
        }

        var sharedEvaluationParameters = new Dictionary<string, object?>
        {
            ["expression"] = request.Source,
            ["awaitPromise"] = request.AwaitPromise,
            ["returnByValue"] = true,
            ["generatePreview"] = false,
            ["userGesture"] = false,
            ["timeout"] = request.Timeout.TotalMilliseconds,
            ["disableBreaks"] = true,
            ["replMode"] = false,
            ["allowUnsafeEvalBlockedByCSP"] = false,
            ["throwOnSideEffect"] = true,
        };
        if (contextId is { } isolatedContextId)
        {
            sharedEvaluationParameters["contextId"] = isolatedContextId;
        }

        using var evaluationReply = await ExecuteAsync(
                "Runtime.evaluate",
                sharedEvaluationParameters)
            .ConfigureAwait(false);
        var evaluation = RequireResult(evaluationReply.RootElement);
        if (evaluation.TryGetProperty("exceptionDetails", out _))
        {
            return NativeBrowserAutomationResult.Rejected("script_exception");
        }

        var remoteObject = evaluation.GetProperty("result");
        if (remoteObject.TryGetProperty("objectId", out _)
            || remoteObject.TryGetProperty("unserializableValue", out _))
        {
            return NativeBrowserAutomationResult.Rejected("script_result_not_serializable");
        }

        var json = remoteObject.TryGetProperty("value", out var value)
            ? value.GetRawText()
            : "null";
        return NativeBrowserAutomationResult.Acknowledged(json);
    }

    private Task DispatchMouseEventAsync(
        string type,
        double x,
        double y,
        BrowserMouseButton button,
        BrowserMouseButtons buttons,
        BrowserInputModifiers modifiers,
        int clickCount,
        double deltaX,
        double deltaY) =>
        ExecuteAcknowledgedAsync(
            "Input.dispatchMouseEvent",
            new
            {
                type,
                x,
                y,
                button = MouseButtonName(button),
                buttons = (int)buttons,
                modifiers = (int)modifiers,
                clickCount,
                deltaX,
                deltaY,
                pointerType = "mouse",
            });

    private Task DispatchKeyEventAsync(
        string type,
        KeyDescriptor key,
        BrowserInputModifiers modifiers) =>
        ExecuteAcknowledgedAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type,
                modifiers = (int)modifiers,
                key = key.Key,
                code = key.Code,
                text = type == "keyDown"
                    && modifiers is BrowserInputModifiers.None
                    ? key.Text
                    : string.Empty,
                unmodifiedText = type == "keyDown"
                    && modifiers is BrowserInputModifiers.None
                    ? key.Text
                    : string.Empty,
                windowsVirtualKeyCode = key.VirtualKeyCode,
                nativeVirtualKeyCode = 0,
                autoRepeat = false,
                isKeypad = false,
                isSystemKey = modifiers.HasFlag(BrowserInputModifiers.Alt),
            });

    private async Task ExecuteAcknowledgedAsync(string method, object parameters)
    {
        using var reply = await ExecuteAsync(method, parameters).ConfigureAwait(false);
        _ = RequireResult(reply.RootElement);
    }

    private async Task<JsonDocument> ExecuteAsync(string method, object? parameters)
    {
        var reply = await _transport.ExecuteAsync(
                method,
                parameters is null ? null : JsonSerializer.Serialize(parameters))
            .ConfigureAwait(false);
        try
        {
            if (StrictUtf8.GetByteCount(reply) > MaximumCdpReplyBytes)
            {
                throw new InvalidOperationException("CEF returned an oversized CDP reply.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidOperationException("CEF returned invalid Unicode.", exception);
        }

        return JsonDocument.Parse(reply);
    }

    private static JsonElement RequireResult(JsonElement reply)
    {
        if (reply.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "CEF rejected the typed command."
                    : "CEF rejected the typed command.");
        }

        return reply.TryGetProperty("result", out var result)
            ? result
            : throw new InvalidOperationException("CEF did not acknowledge the typed command.");
    }

    private static async Task<NativeBrowserAutomationResult> CaptureOutcomeAsync(
        Func<Task<NativeBrowserAutomationResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NativeBrowserAutomationResult.OutcomeUnknown();
        }
    }

    private static string MouseButtonName(BrowserMouseButton button) =>
        button switch
        {
            BrowserMouseButton.None => "none",
            BrowserMouseButton.Left => "left",
            BrowserMouseButton.Right => "right",
            BrowserMouseButton.Middle => "middle",
            BrowserMouseButton.Back => "back",
            BrowserMouseButton.Forward => "forward",
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };

    private static BrowserMouseButtons ButtonFlag(BrowserMouseButton button) =>
        button switch
        {
            BrowserMouseButton.None => BrowserMouseButtons.None,
            BrowserMouseButton.Left => BrowserMouseButtons.Left,
            BrowserMouseButton.Right => BrowserMouseButtons.Right,
            BrowserMouseButton.Middle => BrowserMouseButtons.Middle,
            BrowserMouseButton.Back => BrowserMouseButtons.Back,
            BrowserMouseButton.Forward => BrowserMouseButtons.Forward,
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };

    private sealed record KeyDescriptor(
        string Key,
        string Code,
        int VirtualKeyCode,
        string Text)
    {
        public static KeyDescriptor For(
            BrowserKey value,
            BrowserInputModifiers modifiers)
        {
            var name = value.ToString();
            if (value is >= BrowserKey.A and <= BrowserKey.Z)
            {
                var letter = name[0];
                var shifted = modifiers.HasFlag(BrowserInputModifiers.Shift);
                return new KeyDescriptor(
                    shifted ? name : name.ToLowerInvariant(),
                    "Key" + name,
                    letter,
                    shifted ? name : name.ToLowerInvariant());
            }

            if (value is >= BrowserKey.Digit0 and <= BrowserKey.Digit9)
            {
                var digit = name[^1];
                return new KeyDescriptor(digit.ToString(), name, digit, digit.ToString());
            }

            return value switch
            {
                BrowserKey.Backspace => new("Backspace", "Backspace", 8, ""),
                BrowserKey.Tab => new("Tab", "Tab", 9, "\t"),
                BrowserKey.Enter => new("Enter", "Enter", 13, "\r"),
                BrowserKey.Escape => new("Escape", "Escape", 27, ""),
                BrowserKey.Space => new(" ", "Space", 32, " "),
                BrowserKey.ArrowLeft => new("ArrowLeft", "ArrowLeft", 37, ""),
                BrowserKey.ArrowUp => new("ArrowUp", "ArrowUp", 38, ""),
                BrowserKey.ArrowRight => new("ArrowRight", "ArrowRight", 39, ""),
                BrowserKey.ArrowDown => new("ArrowDown", "ArrowDown", 40, ""),
                BrowserKey.Insert => new("Insert", "Insert", 45, ""),
                BrowserKey.Delete => new("Delete", "Delete", 46, ""),
                BrowserKey.Home => new("Home", "Home", 36, ""),
                BrowserKey.End => new("End", "End", 35, ""),
                BrowserKey.PageUp => new("PageUp", "PageUp", 33, ""),
                BrowserKey.PageDown => new("PageDown", "PageDown", 34, ""),
                BrowserKey.Alt => new("Alt", "AltLeft", 18, ""),
                BrowserKey.Control => new("Control", "ControlLeft", 17, ""),
                BrowserKey.Meta => new("Meta", "MetaLeft", 91, ""),
                BrowserKey.Shift => new("Shift", "ShiftLeft", 16, ""),
                >= BrowserKey.F1 and <= BrowserKey.F12 =>
                    new(name, name, 112 + (value - BrowserKey.F1), ""),
                BrowserKey.Minus => new("-", "Minus", 189, "-"),
                BrowserKey.Equal => new("=", "Equal", 187, "="),
                BrowserKey.BracketLeft => new("[", "BracketLeft", 219, "["),
                BrowserKey.BracketRight => new("]", "BracketRight", 221, "]"),
                BrowserKey.Backslash => new("\\", "Backslash", 220, "\\"),
                BrowserKey.Semicolon => new(";", "Semicolon", 186, ";"),
                BrowserKey.Quote => new("'", "Quote", 222, "'"),
                BrowserKey.Backquote => new("`", "Backquote", 192, "`"),
                BrowserKey.Comma => new(",", "Comma", 188, ","),
                BrowserKey.Period => new(".", "Period", 190, "."),
                BrowserKey.Slash => new("/", "Slash", 191, "/"),
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            };
        }
    }
}
