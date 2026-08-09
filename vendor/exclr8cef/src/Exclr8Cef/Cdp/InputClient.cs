using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>Input</c> domain — high-level input
/// synthesis that's more reliable than raw keystroke injection on
/// real-world sites (debounce traps, autocomplete overwrites,
/// momentum-aware scroll, etc.). Reached via
/// <see cref="CefBrowser.Input"/>.
/// </summary>
public sealed class InputClient : CdpDomainClient
{
    internal InputClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "Input";

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        // Input domain doesn't push events we care about.
    }

    /// <summary>
    /// Insert <paramref name="text"/> at the current input cursor as
    /// if pasted — single atomic insertion, no keystroke-by-keystroke
    /// dispatch. Bypasses keydown/keypress debounce traps and is the
    /// pattern Puppeteer/Playwright use for reliable typing into
    /// autocomplete-aware fields.
    /// </summary>
    /// <remarks>
    /// Doesn't fire individual <c>keydown</c>/<c>keypress</c> events —
    /// the page sees only <c>input</c>/<c>change</c>. If you need a
    /// site to see keystrokes (e.g. it captures hotkeys), use raw
    /// key dispatch instead.
    /// </remarks>
    public Task InsertTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var json = "{\"text\":" + JsonSerializer.Serialize(text) + "}";
        return Browser.ExecuteDevToolsMethodAsync("Input.insertText", json);
    }

    /// <summary>
    /// Synthesize a scroll gesture at (<paramref name="x"/>,
    /// <paramref name="y"/>) with realistic momentum/inertia — the way
    /// touchpad/trackpad/touch scrolling actually behaves, not a
    /// scroll-wheel event. Triggers scroll-snap and infinite-scroll
    /// loaders that programmatic <c>scrollTop</c> doesn't.
    /// </summary>
    /// <param name="x">CSS-pixel viewport coordinate of the gesture origin.</param>
    /// <param name="y">CSS-pixel viewport coordinate of the gesture origin.</param>
    /// <param name="xDistance">Horizontal distance in CSS pixels (positive = right, negative = left).</param>
    /// <param name="yDistance">Vertical distance in CSS pixels (positive = down, negative = up).</param>
    /// <param name="speed">Pixels per second (default 800, CDP's default).</param>
    /// <param name="gestureSource">"default" | "touch" | "mouse"; default lets Chromium pick.</param>
    public Task SynthesizeScrollGestureAsync(
        double x, double y,
        double xDistance = 0, double yDistance = 0,
        int speed = 800,
        string gestureSource = "default")
    {
        var sb = new StringBuilder("{")
            .Append("\"x\":").Append(F(x))
            .Append(",\"y\":").Append(F(y))
            .Append(",\"xDistance\":").Append(F(xDistance))
            .Append(",\"yDistance\":").Append(F(yDistance))
            .Append(",\"speed\":").Append(speed)
            .Append(",\"gestureSourceType\":").Append(JsonSerializer.Serialize(gestureSource))
            .Append('}');
        return Browser.ExecuteDevToolsMethodAsync("Input.synthesizeScrollGesture", sb.ToString());
    }

    /// <summary>
    /// Synthesize a tap gesture at (<paramref name="x"/>,
    /// <paramref name="y"/>). Unlike a mouse click, this is a single
    /// touch tap with a realistic dwell — sites that distinguish touch
    /// vs mouse (mobile UIs, hover-vs-tap menus) react correctly.
    /// </summary>
    /// <param name="duration">Tap duration in ms (default 50).</param>
    /// <param name="tapCount">Number of taps (1 = single, 2 = double).</param>
    public Task SynthesizeTapGestureAsync(double x, double y, int duration = 50, int tapCount = 1)
    {
        var sb = new StringBuilder("{")
            .Append("\"x\":").Append(F(x))
            .Append(",\"y\":").Append(F(y))
            .Append(",\"duration\":").Append(duration)
            .Append(",\"tapCount\":").Append(tapCount)
            .Append('}');
        return Browser.ExecuteDevToolsMethodAsync("Input.synthesizeTapGesture", sb.ToString());
    }

    /// <summary>
    /// Set IME composition state — for non-Latin input flows (CJK,
    /// emoji panels, accent menus) that real keyboards drive through
    /// the platform's IME and that page <c>compositionstart</c> /
    /// <c>compositionupdate</c> / <c>compositionend</c> handlers
    /// observe. Pass an empty string to clear the composition.
    /// </summary>
    public Task SetCompositionAsync(string text, int selectionStart, int selectionEnd, int? replacementStart = null, int? replacementEnd = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder("{")
            .Append("\"text\":").Append(JsonSerializer.Serialize(text))
            .Append(",\"selectionStart\":").Append(selectionStart)
            .Append(",\"selectionEnd\":").Append(selectionEnd);
        if (replacementStart is int rs) sb.Append(",\"replacementStart\":").Append(rs);
        if (replacementEnd   is int re) sb.Append(",\"replacementEnd\":").Append(re);
        sb.Append('}');
        return Browser.ExecuteDevToolsMethodAsync("Input.imeSetComposition", sb.ToString());
    }

    private static string F(double d) => d.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
}
