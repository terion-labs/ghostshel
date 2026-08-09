using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Helpers for the JSON shape CDP uses. Server events look like
/// <c>{"method":"Domain.event","params":{…}}</c>; replies look like
/// <c>{"id":N,"result":{…}}</c>. The domain clients use these to peel
/// off the method name without a full parse, then parse params only if
/// the event is one they actually care about.
/// </summary>
internal static class CdpJson
{
    /// <summary>
    /// Extract the <c>method</c> field from a CDP event JSON string
    /// without a full parse. Returns null if absent.
    /// </summary>
    public static string? GetEventMethod(string json)
    {
        const string Marker = "\"method\":\"";
        int idx = json.IndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += Marker.Length;
        int end = json.IndexOf('"', idx);
        return end < 0 ? null : json.Substring(idx, end - idx);
    }

    /// <summary>
    /// Parse a CDP event JSON and return its <c>params</c> element.
    /// Throws if <c>params</c> is absent — callers should only invoke
    /// this after <see cref="GetEventMethod"/> matched a known method
    /// for which params is guaranteed.
    /// </summary>
    public static JsonElement ParseEventParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // Clone so the JsonDocument can be disposed; caller keeps the element.
        return doc.RootElement.GetProperty("params").Clone();
    }

    /// <summary>
    /// Parse a CDP reply JSON and return its <c>result</c> element. The
    /// caller owns the returned element (cloned, so the inner
    /// JsonDocument is disposed before return). If the reply is a CDP
    /// error (<c>{"id":N,"error":{"code":…,"message":…}}</c>) this throws
    /// <see cref="CdpException"/> with the protocol's own message —
    /// otherwise the caller would see an opaque
    /// <c>KeyNotFoundException("result")</c>.
    /// </summary>
    public static JsonElement ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            int code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
            string message = error.TryGetProperty("message", out var m)
                ? m.GetString() ?? "" : "";
            throw new CdpException(code, message);
        }
        return root.GetProperty("result").Clone();
    }
}

/// <summary>
/// A CDP-level error reply — the browser understood the command and
/// rejected it (unknown method, invalid params, target gone, …).
/// <see cref="Code"/> and <see cref="Message"/> carry the protocol's own
/// error fields.
/// </summary>
public sealed class CdpException : InvalidOperationException
{
    /// <summary>CDP error code (e.g. -32601 method not found).</summary>
    public int Code { get; }

    public CdpException(int code, string message)
        : base($"CDP error {code}: {message}")
    {
        Code = code;
    }
}
