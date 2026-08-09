using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>Target</c> domain — visibility into
/// out-of-process iframes (OOPIFs), service workers, dedicated workers,
/// and other Chromium sub-targets that don't show up in the top-level
/// CDP session. Reached via <see cref="CefBrowser.Target"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> Cross-origin iframes (embedded YouTube,
/// Stripe checkout, reCAPTCHA, etc.) run as separate Chromium
/// processes and are <i>opaque</i> from the top-level CDP session.
/// Without auto-attach the AI sees a black rectangle on the page.
/// </para>
/// <para>
/// <b>Scope of this first cut.</b> This client surfaces auto-attach
/// + the attach/detach events with their session ids and target info,
/// so consumers know an OOPIF appeared and can identify it. Sending
/// commands <i>to</i> sub-sessions (e.g. capturing a screenshot of a
/// specific OOPIF) requires sessionId-routed CDP messages — for now,
/// use <see cref="CefBrowser.SendDevToolsMessageRaw"/> with a
/// <c>"sessionId"</c> field on the envelope JSON. A typed
/// <c>browser.AttachedSession(sessionId)</c> projection is on the
/// roadmap.
/// </para>
/// </remarks>
public sealed class TargetClient : CdpDomainClient
{
    internal TargetClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "Target";

    /// <summary>
    /// Fires when Chromium attaches to a sub-target (OOPIF, worker,
    /// etc.). <c>SessionId</c> identifies the new session for
    /// session-routed CDP commands.
    /// </summary>
    public event EventHandler<TargetAttachedEventArgs>? AttachedToTarget
    {
        add { EnsureEventSubscription(); _attached += value; }
        remove { _attached -= value; }
    }
    private EventHandler<TargetAttachedEventArgs>? _attached;

    /// <summary>Fires when a sub-target is destroyed (navigation, close, etc.).</summary>
    public event EventHandler<TargetDetachedEventArgs>? DetachedFromTarget
    {
        add { EnsureEventSubscription(); _detached += value; }
        remove { _detached -= value; }
    }
    private EventHandler<TargetDetachedEventArgs>? _detached;

    /// <summary>Fires when a target's info changes (URL navigation, title change).</summary>
    public event EventHandler<TargetInfoChangedEventArgs>? TargetInfoChanged
    {
        add { EnsureEventSubscription(); _changed += value; }
        remove { _changed -= value; }
    }
    private EventHandler<TargetInfoChangedEventArgs>? _changed;

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        switch (method)
        {
            case "Target.attachedToTarget":
                if (_attached is null) return;
                var p = CdpJson.ParseEventParams(json);
                _attached.Invoke(this, new TargetAttachedEventArgs(
                    p.GetProperty("sessionId").GetString() ?? "",
                    ParseTargetInfo(p.GetProperty("targetInfo")),
                    p.TryGetProperty("waitingForDebugger", out var w) && w.GetBoolean()));
                break;
            case "Target.detachedFromTarget":
                if (_detached is null) return;
                var d = CdpJson.ParseEventParams(json);
                _detached.Invoke(this, new TargetDetachedEventArgs(
                    d.GetProperty("sessionId").GetString() ?? "",
                    d.TryGetProperty("targetId", out var ti) ? ti.GetString() ?? "" : ""));
                break;
            case "Target.targetInfoChanged":
                if (_changed is null) return;
                var c = CdpJson.ParseEventParams(json);
                _changed.Invoke(this, new TargetInfoChangedEventArgs(ParseTargetInfo(c.GetProperty("targetInfo"))));
                break;
        }
    }

    /// <summary>
    /// Turn auto-attach on so Chromium attaches sessions for every
    /// new sub-target. Defaults match the modern recommendation:
    /// <paramref name="autoAttach"/>=true, no pause-on-attach,
    /// <paramref name="flatten"/>=true (sessionIds rather than
    /// envelope-wrapped messages).
    /// </summary>
    /// <param name="autoAttach">Master toggle.</param>
    /// <param name="waitForDebuggerOnStart">If true, sub-targets are paused at start until <c>Runtime.runIfWaitingForDebugger</c> is sent. Off by default; leave off unless you need to set up CDP before any JS runs in the new target.</param>
    /// <param name="flatten">Use flat session routing (sessionId on each message) instead of envelope-wrapping. On by default — the modern protocol mode.</param>
    public Task SetAutoAttachAsync(bool autoAttach = true, bool waitForDebuggerOnStart = false, bool flatten = true)
    {
        var json = $"{{\"autoAttach\":{Bool(autoAttach)},\"waitForDebuggerOnStart\":{Bool(waitForDebuggerOnStart)},\"flatten\":{Bool(flatten)}}}";
        return Browser.ExecuteDevToolsMethodAsync("Target.setAutoAttach", json);
    }

    /// <summary>
    /// Enumerate all currently-known targets attached to this browser
    /// — top-level page, OOPIFs, dedicated workers, service workers.
    /// </summary>
    public async Task<IReadOnlyList<TargetInfo>> GetTargetsAsync()
    {
        var reply = await Browser.ExecuteDevToolsMethodAsync("Target.getTargets", null);
        var result = CdpJson.ParseResult(reply);
        var list = new List<TargetInfo>();
        if (result.TryGetProperty("targetInfos", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray()) list.Add(ParseTargetInfo(t));
        return list;
    }

    /// <summary>
    /// Detach a session. Use this when you no longer care about a
    /// specific sub-target — auto-attach will re-attach if the target
    /// emits new lifecycle events.
    /// </summary>
    public Task DetachAsync(string sessionId)
        => Browser.ExecuteDevToolsMethodAsync(
            "Target.detachFromTarget",
            "{\"sessionId\":" + JsonSerializer.Serialize(sessionId) + "}");

    private static TargetInfo ParseTargetInfo(JsonElement t)
    {
        return new TargetInfo(
            t.GetProperty("targetId").GetString() ?? "",
            t.GetProperty("type").GetString() ?? "",
            t.GetProperty("title").GetString() ?? "",
            t.GetProperty("url").GetString() ?? "",
            t.TryGetProperty("attached", out var a) && a.GetBoolean(),
            t.TryGetProperty("openerId", out var o) ? o.GetString() : null,
            t.TryGetProperty("browserContextId", out var bc) ? bc.GetString() : null);
    }

    private static string Bool(bool b) => b ? "true" : "false";
}

/// <summary>CDP <c>TargetInfo</c> — identity + type + URL of a CDP-attachable target.</summary>
public sealed record TargetInfo(
    string TargetId,
    /// <summary>"page", "iframe", "worker", "service_worker", "browser", "background_page", "shared_worker", …</summary>
    string Type,
    string Title,
    string Url,
    bool Attached,
    string? OpenerId,
    string? BrowserContextId);

/// <summary>Args for <see cref="TargetClient.AttachedToTarget"/>.</summary>
public sealed class TargetAttachedEventArgs : EventArgs
{
    /// <summary>Session id — pass on CDP commands you send to this sub-target.</summary>
    public string SessionId { get; }
    public TargetInfo Target { get; }
    /// <summary>True if <c>waitForDebuggerOnStart</c> was on and the target is paused at start.</summary>
    public bool WaitingForDebugger { get; }
    internal TargetAttachedEventArgs(string sessionId, TargetInfo target, bool waiting)
    { SessionId = sessionId; Target = target; WaitingForDebugger = waiting; }
}

/// <summary>Args for <see cref="TargetClient.DetachedFromTarget"/>.</summary>
public sealed class TargetDetachedEventArgs : EventArgs
{
    public string SessionId { get; }
    public string TargetId { get; }
    internal TargetDetachedEventArgs(string sessionId, string targetId)
    { SessionId = sessionId; TargetId = targetId; }
}

/// <summary>Args for <see cref="TargetClient.TargetInfoChanged"/>.</summary>
public sealed class TargetInfoChangedEventArgs : EventArgs
{
    public TargetInfo Target { get; }
    internal TargetInfoChangedEventArgs(TargetInfo target) { Target = target; }
}
