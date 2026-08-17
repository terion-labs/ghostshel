using System.Text.Json;
using Exclr8Cef;
using Exclr8Cef.Cdp;

namespace GhostShell.Browser;

/// <summary>
/// Executes semantic browser operations through acknowledged CDP round trips.
/// No page-realm selector, script, or DOM event shortcut is used for input.
/// </summary>
internal sealed class CefSemanticBrowser(CefBrowser browser)
    : ICefSemanticBrowser
{
    private readonly CefBrowser _browser = browser
        ?? throw new ArgumentNullException(nameof(browser));
    private bool _domainsEnabled;

    public async Task<IReadOnlyList<CefSemanticNode>>
        ReadAccessibilityTreeAsync()
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var nodes = await _browser.Accessibility
            .GetFullTreeAsync(maxDepth: 64)
            .ConfigureAwait(false);
        return nodes.Select(Project).ToArray();
    }

    public async Task<CefSemanticNode?> ReadAccessibilityNodeAsync(
        int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var nodes = await _browser.Accessibility
            .GetPartialTreeAsync(backendNodeId, fetchRelatives: true)
            .ConfigureAwait(false);
        var node = nodes.FirstOrDefault(
            candidate => candidate.BackendDomNodeId == backendNodeId);
        return node is null ? null : Project(node);
    }

    public async Task<CefSemanticPoint?> PrepareClickPointAsync(
        int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        await _browser.Dom
            .ScrollIntoViewAsync(backendNodeId)
            .ConfigureAwait(false);
        var quads = await _browser.Dom
            .GetContentQuadsAsync(backendNodeId)
            .ConfigureAwait(false);
        foreach (var quad in quads)
        {
            var clip = quad.ToBoundingClip();
            if (clip.Width > 1
                && clip.Height > 1
                && double.IsFinite(clip.X)
                && double.IsFinite(clip.Y)
                && double.IsFinite(clip.Width)
                && double.IsFinite(clip.Height))
            {
                return new CefSemanticPoint(
                    clip.X + clip.Width / 2,
                    clip.Y + clip.Height / 2);
            }
        }

        return null;
    }

    public async Task<bool> HitTestIncludesAsync(
        CefSemanticPoint point,
        int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var hitNodeId = await _browser.Dom
            .GetNodeForLocationAsync(
                checked((int)Math.Round(point.X)),
                checked((int)Math.Round(point.Y)))
            .ConfigureAwait(false);
        if (hitNodeId is null)
        {
            return false;
        }

        if (hitNodeId.Value == backendNodeId)
        {
            return true;
        }

        var relatives = await _browser.Accessibility
            .GetPartialTreeAsync(hitNodeId.Value, fetchRelatives: true)
            .ConfigureAwait(false);
        return relatives.Any(
            node => node.BackendDomNodeId == backendNodeId);
    }

    public async Task DispatchClickAsync(CefSemanticPoint point)
    {
        await ExecuteAcknowledgedAsync(
                "Input.dispatchMouseEvent",
                new
                {
                    type = "mouseMoved",
                    x = point.X,
                    y = point.Y,
                })
            .ConfigureAwait(false);
        await ExecuteAcknowledgedAsync(
                "Input.dispatchMouseEvent",
                new
                {
                    type = "mousePressed",
                    x = point.X,
                    y = point.Y,
                    button = "left",
                    buttons = 1,
                    clickCount = 1,
                })
            .ConfigureAwait(false);
        await ExecuteAcknowledgedAsync(
                "Input.dispatchMouseEvent",
                new
                {
                    type = "mouseReleased",
                    x = point.X,
                    y = point.Y,
                    button = "left",
                    buttons = 0,
                    clickCount = 1,
                })
            .ConfigureAwait(false);
    }

    public async Task ReplaceFocusedTextAsync(
        int backendNodeId,
        string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        await _browser.Dom.FocusAsync(backendNodeId).ConfigureAwait(false);
        await DispatchKeyAsync(
                "rawKeyDown",
                "a",
                "KeyA",
                65,
                commands: ["SelectAll"])
            .ConfigureAwait(false);
        await DispatchKeyAsync(
                "keyUp",
                "a",
                "KeyA",
                65)
            .ConfigureAwait(false);
        await DispatchKeyAsync(
                "rawKeyDown",
                "Backspace",
                "Backspace",
                8)
            .ConfigureAwait(false);
        await DispatchKeyAsync(
                "keyUp",
                "Backspace",
                "Backspace",
                8)
            .ConfigureAwait(false);
        if (text.Length != 0)
        {
            // Input.insertText is Chromium's acknowledged, trusted input path;
            // it fires editing/input behavior without assigning page state.
            await _browser.Input.InsertTextAsync(text).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsVisibleAsync(int backendNodeId)
    {
        await EnsureDomainsEnabledAsync().ConfigureAwait(false);
        var quads = await _browser.Dom
            .GetContentQuadsAsync(backendNodeId)
            .ConfigureAwait(false);
        foreach (var quad in quads)
        {
            var clip = quad.ToBoundingClip();
            if (clip.Width <= 1
                || clip.Height <= 1
                || !double.IsFinite(clip.X)
                || !double.IsFinite(clip.Y)
                || !double.IsFinite(clip.Width)
                || !double.IsFinite(clip.Height))
            {
                continue;
            }

            var point = new CefSemanticPoint(
                clip.X + clip.Width / 2,
                clip.Y + clip.Height / 2);
            if (await HitTestIncludesAsync(point, backendNodeId)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnsureDomainsEnabledAsync()
    {
        if (_domainsEnabled)
        {
            return;
        }

        await _browser.Accessibility.EnableAsync().ConfigureAwait(false);
        await _browser.Dom.EnableAsync().ConfigureAwait(false);
        _domainsEnabled = true;
    }

    private Task DispatchKeyAsync(
        string type,
        string key,
        string code,
        int windowsVirtualKeyCode,
        string text = "",
        IReadOnlyList<string>? commands = null) =>
        ExecuteAcknowledgedAsync(
            "Input.dispatchKeyEvent",
            SerializeKeyEvent(
                type,
                key,
                code,
                windowsVirtualKeyCode,
                text,
                commands));

    internal static string SerializeKeyEvent(
        string type,
        string key,
        string code,
        int windowsVirtualKeyCode,
        string text = "",
        IReadOnlyList<string>? commands = null) =>
        commands is null
            ? JsonSerializer.Serialize(
                new
                {
                    type,
                    key,
                    code,
                    text,
                    unmodifiedText = text,
                    windowsVirtualKeyCode,
                    nativeVirtualKeyCode = 0,
                })
            : JsonSerializer.Serialize(
                new
                {
                    type,
                    key,
                    code,
                    text,
                    unmodifiedText = text,
                    windowsVirtualKeyCode,
                    nativeVirtualKeyCode = 0,
                    commands,
                });

    private async Task ExecuteAcknowledgedAsync(
        string method,
        object parameters) =>
        await ExecuteAcknowledgedAsync(
                method,
                JsonSerializer.Serialize(parameters))
            .ConfigureAwait(false);

    private async Task ExecuteAcknowledgedAsync(
        string method,
        string parametersJson)
    {
        var reply = await _browser.ExecuteDevToolsMethodAsync(
                method,
                parametersJson)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(reply);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                error.TryGetProperty("message", out var message)
                    ? message.GetString()
                        ?? "CEF rejected an input command."
                    : "CEF rejected an input command.");
        }

        if (!document.RootElement.TryGetProperty("result", out _))
        {
            throw new InvalidOperationException(
                "CEF did not acknowledge an input command.");
        }
    }

    private static CefSemanticNode Project(AxNode node) =>
        new(
            node.Id,
            node.BackendDomNodeId,
            node.Ignored,
            node.Role,
            node.Name,
            node.ParentId,
            node.ChildIds,
            node.Properties,
            node.Value);
}
