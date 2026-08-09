using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>PerformanceTimeline</c> domain — web
/// vitals as live events: largest-contentful-paint (with the LCP
/// element's <c>backendNodeId</c>), layout-shift (with attribution),
/// long animation frames, etc. Reached via
/// <see cref="CefBrowser.PerformanceTimeline"/>.
/// </summary>
/// <remarks>
/// The LCP element is "the hero of the page" — very useful as a
/// framing signal for summarization. Layout-shift attribution tells
/// you when content moved out from under a click target.
/// </remarks>
public sealed class PerformanceTimelineClient : CdpDomainClient
{
    internal PerformanceTimelineClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "PerformanceTimeline";

    /// <summary>
    /// Fires when Blink emits a Performance Timeline entry of one of
    /// the event types you enabled via <see cref="EnableAsync"/>. The
    /// raw <c>params.event</c> JSON element is included so callers can
    /// reach type-specific fields (e.g. LCP <c>nodeId</c>, layout-shift
    /// <c>value</c>) without a separate parse step.
    /// </summary>
    public event EventHandler<TimelineEventArgs>? TimelineEventAdded
    {
        add { EnsureEventSubscription(); _added += value; }
        remove { _added -= value; }
    }
    private EventHandler<TimelineEventArgs>? _added;

    /// <summary>
    /// Subscribe to the Performance Timeline entries you care about.
    /// Common picks: <c>"largest-contentful-paint"</c>, <c>"layout-shift"</c>,
    /// <c>"first-input"</c>, <c>"long-animation-frame"</c>,
    /// <c>"longtask"</c>, <c>"event"</c>, <c>"navigation"</c>,
    /// <c>"paint"</c>, <c>"resource"</c>, <c>"mark"</c>, <c>"measure"</c>.
    /// </summary>
    public Task EnableAsync(params string[] eventTypes)
    {
        var sb = new StringBuilder("{\"eventTypes\":[");
        for (int i = 0; i < eventTypes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(eventTypes[i]));
        }
        sb.Append("]}");
        return Browser.ExecuteDevToolsMethodAsync("PerformanceTimeline.enable", sb.ToString());
    }

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        if (method != "PerformanceTimeline.timelineEventAdded") return;
        if (_added is null) return;
        var p = CdpJson.ParseEventParams(json);
        if (!p.TryGetProperty("event", out var evt)) return;
        _added.Invoke(this, new TimelineEventArgs(
            evt.TryGetProperty("name",  out var n)  ? n.GetString() ?? ""  : "",
            evt.TryGetProperty("type",  out var t)  ? t.GetString() ?? ""  : "",
            evt.TryGetProperty("time",  out var tt) ? tt.GetDouble()       : 0,
            evt));
    }
}

/// <summary>
/// Args for <see cref="PerformanceTimelineClient.TimelineEventAdded"/>.
/// <see cref="Raw"/> is the full <c>params.event</c> CDP element —
/// inspect <c>Raw.GetProperty("nodeId")</c> (LCP) or
/// <c>Raw.GetProperty("value")</c> (layout-shift) for type-specific
/// fields without a separate parse.
/// </summary>
public sealed class TimelineEventArgs : EventArgs
{
    /// <summary>Performance entry name (often the URL for resource entries).</summary>
    public string Name { get; }
    /// <summary>Performance entry type ("largest-contentful-paint", "layout-shift", …).</summary>
    public string Type { get; }
    /// <summary>Entry start time, milliseconds (DOMHighResTimeStamp semantics).</summary>
    public double Time { get; }
    /// <summary>The raw CDP <c>event</c> object for type-specific fields.</summary>
    public JsonElement Raw { get; }

    internal TimelineEventArgs(string name, string type, double time, JsonElement raw)
    {
        Name = name; Type = type; Time = time; Raw = raw;
    }
}
