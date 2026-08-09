using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>Page</c> domain — page lifecycle events,
/// rich screenshot options (clip + beyondViewport), and other
/// page-scoped operations. Reached via <see cref="CefBrowser.Page"/>.
/// </summary>
public sealed class PageClient : CdpDomainClient
{
    internal PageClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "Page";

    // ---- Lifecycle events -------------------------------------------

    /// <summary>
    /// Per-frame lifecycle markers from Blink: <c>init</c>,
    /// <c>firstPaint</c>, <c>firstContentfulPaint</c>,
    /// <c>firstMeaningfulPaint</c>, <c>DOMContentLoaded</c>, <c>load</c>,
    /// <c>networkIdle</c>, <c>networkAlmostIdle</c>. Use this in place
    /// of <see cref="CefBrowser.LoadEnd"/> for SPA-aware "page is
    /// settled" detection (<c>LoadEnd</c> barely fires on SPAs;
    /// lifecycle events fire continuously).
    /// </summary>
    /// <remarks>
    /// Requires <see cref="SetLifecycleEventsEnabledAsync"/> = true.
    /// CDP doesn't auto-enable; calling that once after the page is
    /// loaded is enough — the setting sticks across navigations.
    /// </remarks>
    public event EventHandler<LifecycleEventArgs>? LifecycleEvent
    {
        add { EnsureEventSubscription(); _lifecycle += value; }
        remove { _lifecycle -= value; }
    }
    private EventHandler<LifecycleEventArgs>? _lifecycle;

    /// <summary>
    /// Turn lifecycle-event reporting on/off. Off by default — call
    /// once after subscribing to <see cref="LifecycleEvent"/>.
    /// </summary>
    public Task SetLifecycleEventsEnabledAsync(bool enabled = true)
        => Browser.ExecuteDevToolsMethodAsync(
            "Page.setLifecycleEventsEnabled",
            "{\"enabled\":" + (enabled ? "true" : "false") + "}");

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        switch (method)
        {
            case "Page.lifecycleEvent":
                if (_lifecycle is null) return;
                var p = CdpJson.ParseEventParams(json);
                _lifecycle.Invoke(this, new LifecycleEventArgs(
                    p.TryGetProperty("frameId", out var fid) ? fid.GetString() ?? "" : "",
                    p.TryGetProperty("loaderId", out var lid) ? lid.GetString() ?? "" : "",
                    p.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                    p.TryGetProperty("timestamp", out var ts) ? ts.GetDouble() : 0));
                break;

            case "Page.screencastFrame":
                DispatchScreencastFrame(json);
                break;

            case "Page.screencastVisibilityChanged":
                if (_screencastVisibility is null) return;
                var v = CdpJson.ParseEventParams(json);
                _screencastVisibility.Invoke(this, v.GetProperty("visible").GetBoolean());
                break;
        }
    }

    // ---- Screencast --------------------------------------------------

    /// <summary>
    /// Fires for each frame the screencast emits — base64 JPEG/PNG +
    /// per-frame metadata (scroll offset, page scale, viewport size,
    /// timestamp). The handler MUST keep references brief and is
    /// expected to either ACK promptly (auto-ACK is on by default) or
    /// take over ACK manually via <see cref="ManualAck"/>.
    /// </summary>
    /// <remarks>
    /// Chromium's screencast is change-driven + back-pressured: a new
    /// frame doesn't ship until the previous one is ACKed. With
    /// auto-ACK on (default) we ACK immediately after raising the
    /// event so the next frame can flow; with auto-ACK off, the
    /// handler owns pacing by calling <see cref="AckScreencastFrameAsync"/>.
    /// </remarks>
    public event EventHandler<ScreencastFrameEventArgs>? ScreencastFrame
    {
        add { EnsureEventSubscription(); _screencastFrame += value; }
        remove { _screencastFrame -= value; }
    }
    private EventHandler<ScreencastFrameEventArgs>? _screencastFrame;

    /// <summary>Fires when the tab visibility flips (foreground / background).</summary>
    public event EventHandler<bool>? ScreencastVisibilityChanged
    {
        add { EnsureEventSubscription(); _screencastVisibility += value; }
        remove { _screencastVisibility -= value; }
    }
    private EventHandler<bool>? _screencastVisibility;

    /// <summary>
    /// If true, <see cref="ScreencastFrame"/> handlers are responsible
    /// for calling <see cref="AckScreencastFrameAsync"/>. Default false
    /// — we auto-ACK after invoking handlers. Switch on if your
    /// consumer can be slow and you want the ACK to gate the next frame
    /// (effectively rate-limits the stream to consumer speed).
    /// </summary>
    public bool ManualAck { get; set; }

    /// <summary>
    /// Start the CDP screencast — Chromium's already-throttled
    /// change-driven frame stream. ~10x cheaper bytes/sec than full
    /// per-paint capture for "AI is watching" use cases.
    /// </summary>
    /// <param name="format">"jpeg" (smaller, lossy — default) or "png" (lossless, ~10x larger).</param>
    /// <param name="quality">JPEG quality 0-100; default 80.</param>
    /// <param name="maxWidth">Cap on the longest viewport side in CSS pixels; null = full viewport.</param>
    /// <param name="maxHeight">Cap on the shorter viewport side in CSS pixels; null = full viewport.</param>
    /// <param name="everyNthFrame">Emit only every Nth compositor frame (1 = every frame, 2 = half, …). Lets you cap fps.</param>
    public Task StartScreencastAsync(
        string format = "jpeg",
        int quality = 80,
        int? maxWidth = null,
        int? maxHeight = null,
        int everyNthFrame = 1)
    {
        var sb = new StringBuilder("{\"format\":").Append(JsonSerializer.Serialize(format))
            .Append(",\"quality\":").Append(quality)
            .Append(",\"everyNthFrame\":").Append(everyNthFrame);
        if (maxWidth  is int w) sb.Append(",\"maxWidth\":").Append(w);
        if (maxHeight is int h) sb.Append(",\"maxHeight\":").Append(h);
        sb.Append('}');
        return Browser.ExecuteDevToolsMethodAsync("Page.startScreencast", sb.ToString());
    }

    /// <summary>Stop the screencast — Chromium stops emitting frames.</summary>
    public Task StopScreencastAsync()
        => Browser.ExecuteDevToolsMethodAsync("Page.stopScreencast", null);

    /// <summary>
    /// Manually ACK the screencast frame with <paramref name="sessionId"/>.
    /// Only needed when <see cref="ManualAck"/> is on; the default
    /// auto-ACK fires this immediately after raising
    /// <see cref="ScreencastFrame"/>.
    /// </summary>
    public Task AckScreencastFrameAsync(int sessionId)
        => Browser.ExecuteDevToolsMethodAsync(
            "Page.screencastFrameAck",
            "{\"sessionId\":" + sessionId + "}");

    private void DispatchScreencastFrame(string json)
    {
        var p = CdpJson.ParseEventParams(json);
        int sessionId = p.GetProperty("sessionId").GetInt32();

        // Chromium won't send the next frame until this one is ACKed, so
        // every code path below must end in an ACK (unless the host opted
        // into ManualAck): no handler subscribed (the last handler may
        // have been removed while the cast was running), or a handler
        // that throws — either would otherwise wedge the stream forever.
        var handler = _screencastFrame;
        if (handler is null)
        {
            _ = AckScreencastFrameAsync(sessionId);
            return;
        }
        try
        {
            string data = p.GetProperty("data").GetString() ?? "";
            var md = p.GetProperty("metadata");
            var args = new ScreencastFrameEventArgs(
                sessionId,
                data,
                md.TryGetProperty("offsetTop",       out var ot) ? ot.GetDouble() : 0,
                md.TryGetProperty("pageScaleFactor", out var pf) ? pf.GetDouble() : 1,
                md.TryGetProperty("deviceWidth",     out var dw) ? dw.GetDouble() : 0,
                md.TryGetProperty("deviceHeight",    out var dh) ? dh.GetDouble() : 0,
                md.TryGetProperty("scrollOffsetX",   out var sx) ? sx.GetDouble() : 0,
                md.TryGetProperty("scrollOffsetY",   out var sy) ? sy.GetDouble() : 0,
                md.TryGetProperty("timestamp",       out var ts) ? ts.GetDouble() : 0);
            handler.Invoke(this, args);
        }
        finally
        {
            // Auto-ACK so the next frame can flow. Fire-and-forget — if
            // the browser is going down the ACK will fail harmlessly.
            if (!ManualAck) _ = AckScreencastFrameAsync(sessionId);
        }
    }

    // ---- Screenshot -------------------------------------------------

    /// <summary>
    /// CDP <c>Page.captureScreenshot</c> with the full option surface —
    /// element-clipped screenshots, full-page (beyond viewport),
    /// speed/quality trade-off. Pair with <see cref="DomClient.GetBoxModelAsync"/>
    /// to screenshot just one element.
    /// </summary>
    /// <param name="format">"png" (default) | "jpeg" | "webp".</param>
    /// <param name="quality">0–100, for jpeg/webp only (ignored for png).</param>
    /// <param name="clip">Optional sub-region in CSS pixels; null = whole viewport.</param>
    /// <param name="captureBeyondViewport">If true, captures the full document height (true full-page).</param>
    /// <param name="optimizeForSpeed">If true, trades fidelity for capture speed (good for streaming).</param>
    /// <returns>Raw image bytes ready to write to disk or hand to a vision API.</returns>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread —
    /// marshal back before touching browser state.
    /// </remarks>
    public async Task<byte[]> CaptureScreenshotAsync(
        string format = "png",
        int? quality = null,
        ScreenshotClip? clip = null,
        bool captureBeyondViewport = false,
        bool optimizeForSpeed = false)
    {
        var sb = new StringBuilder("{\"format\":").Append(JsonSerializer.Serialize(format));
        if (quality is int q) sb.Append(",\"quality\":").Append(q);
        if (captureBeyondViewport) sb.Append(",\"captureBeyondViewport\":true");
        if (optimizeForSpeed) sb.Append(",\"optimizeForSpeed\":true");
        if (clip is ScreenshotClip c)
        {
            sb.Append(",\"clip\":{\"x\":").Append(c.X.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"y\":").Append(c.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"width\":").Append(c.Width.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"height\":").Append(c.Height.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"scale\":").Append(c.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append('}');

        var reply = await Browser.ExecuteDevToolsMethodAsync("Page.captureScreenshot", sb.ToString());
        var result = CdpJson.ParseResult(reply);
        return Convert.FromBase64String(result.GetProperty("data").GetString() ?? "");
    }
}

/// <summary>
/// A CSS-pixel rectangle for <see cref="PageClient.CaptureScreenshotAsync"/>.
/// <paramref name="Scale"/> is the device-pixel multiplier (1.0 = 1
/// physical pixel per CSS pixel; 2.0 = retina). Defaults to 1.
/// </summary>
public readonly record struct ScreenshotClip(double X, double Y, double Width, double Height, double Scale = 1.0);

/// <summary>
/// One frame from <see cref="PageClient.ScreencastFrame"/> — base64
/// JPEG/PNG payload plus the metadata needed to correlate it with
/// scroll position and viewport size at frame time.
/// </summary>
public sealed class ScreencastFrameEventArgs : EventArgs
{
    /// <summary>CDP session id — pass to <see cref="PageClient.AckScreencastFrameAsync"/>.</summary>
    public int SessionId { get; }
    /// <summary>Base64-encoded image bytes — decode with <see cref="GetImageBytes"/>.</summary>
    public string DataBase64 { get; }
    /// <summary>Top-of-page offset of the viewport in CSS pixels (= scroll position when not at the top).</summary>
    public double OffsetTop { get; }
    /// <summary>Current page scale factor (pinch zoom). 1.0 = no zoom.</summary>
    public double PageScaleFactor { get; }
    public double DeviceWidth { get; }
    public double DeviceHeight { get; }
    public double ScrollOffsetX { get; }
    public double ScrollOffsetY { get; }
    /// <summary>CDP monotonic timestamp (seconds).</summary>
    public double Timestamp { get; }

    internal ScreencastFrameEventArgs(
        int sessionId, string dataBase64,
        double offsetTop, double pageScaleFactor,
        double deviceWidth, double deviceHeight,
        double scrollOffsetX, double scrollOffsetY,
        double timestamp)
    {
        SessionId = sessionId; DataBase64 = dataBase64;
        OffsetTop = offsetTop; PageScaleFactor = pageScaleFactor;
        DeviceWidth = deviceWidth; DeviceHeight = deviceHeight;
        ScrollOffsetX = scrollOffsetX; ScrollOffsetY = scrollOffsetY;
        Timestamp = timestamp;
    }

    /// <summary>Decode the base64 payload to raw image bytes.</summary>
    public byte[] GetImageBytes() => Convert.FromBase64String(DataBase64);
}

/// <summary>Args for <see cref="PageClient.LifecycleEvent"/>.</summary>
public sealed class LifecycleEventArgs : EventArgs
{
    /// <summary>The frame id this event fired on. Main frame is the typical case.</summary>
    public string FrameId { get; }
    /// <summary>Loader id (a CDP id distinct from frame id).</summary>
    public string LoaderId { get; }
    /// <summary>One of: init, firstPaint, firstContentfulPaint, firstMeaningfulPaint, DOMContentLoaded, load, networkIdle, networkAlmostIdle.</summary>
    public string Name { get; }
    /// <summary>Monotonic timestamp in seconds since CDP attachment.</summary>
    public double Timestamp { get; }

    internal LifecycleEventArgs(string frameId, string loaderId, string name, double timestamp)
    {
        FrameId = frameId; LoaderId = loaderId; Name = name; Timestamp = timestamp;
    }
}
