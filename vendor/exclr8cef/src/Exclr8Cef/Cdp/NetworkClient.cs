using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>Network</c> domain — request/response
/// metadata as live events plus on-demand body retrieval. The
/// "devtools Network panel" surface, in code. Reached via
/// <see cref="CefBrowser.Network"/>.
/// </summary>
/// <remarks>
/// <para>
/// Major AI unlock: instead of scraping rendered DOM to read what
/// the page is showing, subscribe to <see cref="ResponseReceived"/> /
/// <see cref="LoadingFinished"/> and read the underlying API JSON
/// directly via <see cref="GetResponseBodyAsync"/>. Far more reliable
/// than UI scraping on data-heavy sites.
/// </para>
/// <para>
/// Coexists with <see cref="CefBrowser.ResourceRequest"/> /
/// <see cref="CefBrowser.ResourceRequestObserved"/> — those are
/// CEF's request-handler surface (gates / observers fired from CEF's
/// IO thread). This is CDP's view: richer metadata, body retrieval,
/// and CDP-specific requestIds. Use whichever fits.
/// </para>
/// </remarks>
public sealed class NetworkClient : CdpDomainClient
{
    internal NetworkClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "Network";

    /// <summary>
    /// Fires when a request is about to be sent. <c>RequestId</c> is
    /// a CDP-specific id (string); use it for follow-up calls like
    /// <see cref="GetResponseBodyAsync"/>.
    /// </summary>
    public event EventHandler<NetworkRequestEventArgs>? RequestWillBeSent
    {
        add { EnsureEventSubscription(); _willBeSent += value; }
        remove { _willBeSent -= value; }
    }
    private EventHandler<NetworkRequestEventArgs>? _willBeSent;

    /// <summary>
    /// Fires when response headers + status are available. Body
    /// isn't ready yet — wait for <see cref="LoadingFinished"/>.
    /// </summary>
    public event EventHandler<NetworkResponseEventArgs>? ResponseReceived
    {
        add { EnsureEventSubscription(); _responseReceived += value; }
        remove { _responseReceived -= value; }
    }
    private EventHandler<NetworkResponseEventArgs>? _responseReceived;

    /// <summary>
    /// Fires when the response body is fully downloaded and
    /// <see cref="GetResponseBodyAsync"/> will succeed.
    /// </summary>
    public event EventHandler<NetworkLoadingFinishedEventArgs>? LoadingFinished
    {
        add { EnsureEventSubscription(); _loadingFinished += value; }
        remove { _loadingFinished -= value; }
    }
    private EventHandler<NetworkLoadingFinishedEventArgs>? _loadingFinished;

    /// <summary>
    /// Fires when a request fails (network error, blocked, cancelled).
    /// </summary>
    public event EventHandler<NetworkLoadingFailedEventArgs>? LoadingFailed
    {
        add { EnsureEventSubscription(); _loadingFailed += value; }
        remove { _loadingFailed -= value; }
    }
    private EventHandler<NetworkLoadingFailedEventArgs>? _loadingFailed;

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        switch (method)
        {
            case "Network.requestWillBeSent":
                if (_willBeSent is null) return;
                var rp = CdpJson.ParseEventParams(json);
                var req = rp.GetProperty("request");
                _willBeSent.Invoke(this, new NetworkRequestEventArgs(
                    rp.GetProperty("requestId").GetString() ?? "",
                    req.GetProperty("url").GetString() ?? "",
                    req.GetProperty("method").GetString() ?? "GET",
                    rp.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
                    ReadHeaders(req),
                    rp.TryGetProperty("timestamp", out var ts) ? ts.GetDouble() : 0));
                break;

            case "Network.responseReceived":
                if (_responseReceived is null) return;
                var pp = CdpJson.ParseEventParams(json);
                var rsp = pp.GetProperty("response");
                _responseReceived.Invoke(this, new NetworkResponseEventArgs(
                    pp.GetProperty("requestId").GetString() ?? "",
                    rsp.GetProperty("url").GetString() ?? "",
                    rsp.TryGetProperty("status", out var st) ? st.GetInt32() : 0,
                    rsp.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "",
                    ReadHeaders(rsp),
                    pp.TryGetProperty("type", out var ty2) ? ty2.GetString() ?? "" : "",
                    rsp.TryGetProperty("fromDiskCache", out var fdc) && fdc.GetBoolean(),
                    rsp.TryGetProperty("fromServiceWorker", out var fsw) && fsw.GetBoolean()));
                break;

            case "Network.loadingFinished":
                if (_loadingFinished is null) return;
                var lp = CdpJson.ParseEventParams(json);
                _loadingFinished.Invoke(this, new NetworkLoadingFinishedEventArgs(
                    lp.GetProperty("requestId").GetString() ?? "",
                    lp.TryGetProperty("encodedDataLength", out var len) ? len.GetDouble() : 0,
                    lp.TryGetProperty("timestamp", out var ts2) ? ts2.GetDouble() : 0));
                break;

            case "Network.loadingFailed":
                if (_loadingFailed is null) return;
                var fp = CdpJson.ParseEventParams(json);
                _loadingFailed.Invoke(this, new NetworkLoadingFailedEventArgs(
                    fp.GetProperty("requestId").GetString() ?? "",
                    fp.TryGetProperty("errorText", out var et) ? et.GetString() ?? "" : "",
                    fp.TryGetProperty("canceled", out var cn) && cn.GetBoolean(),
                    fp.TryGetProperty("blockedReason", out var br) ? br.GetString() ?? "" : ""));
                break;
        }
    }

    /// <summary>
    /// Enable the Network domain. Required before request/response
    /// events fire or body retrieval works. Optional CDP knobs
    /// (<c>maxTotalBufferSize</c>, <c>maxResourceBufferSize</c>)
    /// default to CDP's defaults — call
    /// <see cref="CefBrowser.ExecuteDevToolsMethodAsync"/> directly
    /// if you need to tune them.
    /// </summary>
    public Task EnableAsync()
        => Browser.ExecuteDevToolsMethodAsync("Network.enable", null);

    /// <summary>Disable the Network domain.</summary>
    public Task DisableAsync()
        => Browser.ExecuteDevToolsMethodAsync("Network.disable", null);

    /// <summary>
    /// Retrieve the response body for a finished request. Wait for
    /// <see cref="LoadingFinished"/> for the matching requestId
    /// before calling, or you'll get a "no resource with given
    /// identifier" error.
    /// </summary>
    /// <param name="requestId">CDP requestId from one of the request events.</param>
    /// <returns>The body. If <see cref="NetworkResponseBody.Base64Encoded"/> is true, decode before use (binary or non-UTF-8 content).</returns>
    public async Task<NetworkResponseBody> GetResponseBodyAsync(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        var json = "{\"requestId\":" + JsonSerializer.Serialize(requestId) + "}";
        var reply = await Browser.ExecuteDevToolsMethodAsync("Network.getResponseBody", json);
        var r = CdpJson.ParseResult(reply);
        return new NetworkResponseBody(
            r.GetProperty("body").GetString() ?? "",
            r.TryGetProperty("base64Encoded", out var b) && b.GetBoolean());
    }

    /// <summary>
    /// Retrieve the request POST body for a request that had one.
    /// Returns null if the request had no body.
    /// </summary>
    public async Task<string?> GetRequestPostDataAsync(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        try
        {
            var json = "{\"requestId\":" + JsonSerializer.Serialize(requestId) + "}";
            var reply = await Browser.ExecuteDevToolsMethodAsync("Network.getRequestPostData", json);
            return CdpJson.ParseResult(reply).GetProperty("postData").GetString();
        }
        catch
        {
            // CDP errors when the request had no POST data — translate to null.
            return null;
        }
    }

    /// <summary>
    /// Inject extra HTTP headers on every subsequent request from
    /// this browser. Replaces the entire extra-header set (pass an
    /// empty dictionary to clear).
    /// </summary>
    public Task SetExtraHeadersAsync(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var sb = new StringBuilder("{\"headers\":{");
        bool first = true;
        foreach (var kv in headers)
        {
            if (!first) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':').Append(JsonSerializer.Serialize(kv.Value));
            first = false;
        }
        sb.Append("}}");
        return Browser.ExecuteDevToolsMethodAsync("Network.setExtraHTTPHeaders", sb.ToString());
    }

    /// <summary>
    /// Override the User-Agent for this browser.
    /// </summary>
    public Task SetUserAgentAsync(string userAgent, string? acceptLanguage = null, string? platform = null)
    {
        var sb = new StringBuilder("{\"userAgent\":").Append(JsonSerializer.Serialize(userAgent));
        if (acceptLanguage is not null) sb.Append(",\"acceptLanguage\":").Append(JsonSerializer.Serialize(acceptLanguage));
        if (platform       is not null) sb.Append(",\"platform\":").Append(JsonSerializer.Serialize(platform));
        sb.Append('}');
        return Browser.ExecuteDevToolsMethodAsync("Network.setUserAgentOverride", sb.ToString());
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(JsonElement obj)
    {
        if (!obj.TryGetProperty("headers", out var h) || h.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in h.EnumerateObject())
        {
            // Header values can be string or string-array (multi-valued).
            d[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Array  => string.Join(", ", prop.Value.EnumerateArray().Select(v => v.GetString() ?? "")),
                _                    => prop.Value.GetRawText()
            };
        }
        return d;
    }
}

/// <summary>Args for <see cref="NetworkClient.RequestWillBeSent"/>.</summary>
public sealed class NetworkRequestEventArgs : EventArgs
{
    /// <summary>CDP requestId — use this for <see cref="NetworkClient.GetResponseBodyAsync"/>.</summary>
    public string RequestId { get; }
    public string Url { get; }
    public string Method { get; }
    /// <summary>CDP resource type — "Document", "Stylesheet", "Script", "XHR", "Fetch", "Image", etc.</summary>
    public string ResourceType { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    /// <summary>Monotonic CDP timestamp (seconds).</summary>
    public double Timestamp { get; }

    internal NetworkRequestEventArgs(string requestId, string url, string method,
        string resourceType, IReadOnlyDictionary<string, string> headers, double timestamp)
    {
        RequestId = requestId; Url = url; Method = method; ResourceType = resourceType;
        Headers = headers; Timestamp = timestamp;
    }
}

/// <summary>Args for <see cref="NetworkClient.ResponseReceived"/>.</summary>
public sealed class NetworkResponseEventArgs : EventArgs
{
    public string RequestId { get; }
    public string Url { get; }
    public int Status { get; }
    public string MimeType { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public string ResourceType { get; }
    public bool FromDiskCache { get; }
    public bool FromServiceWorker { get; }

    internal NetworkResponseEventArgs(string requestId, string url, int status,
        string mimeType, IReadOnlyDictionary<string, string> headers, string resourceType,
        bool fromDiskCache, bool fromServiceWorker)
    {
        RequestId = requestId; Url = url; Status = status; MimeType = mimeType;
        Headers = headers; ResourceType = resourceType;
        FromDiskCache = fromDiskCache; FromServiceWorker = fromServiceWorker;
    }
}

/// <summary>Args for <see cref="NetworkClient.LoadingFinished"/>.</summary>
public sealed class NetworkLoadingFinishedEventArgs : EventArgs
{
    public string RequestId { get; }
    /// <summary>On-the-wire byte count (post-compression).</summary>
    public double EncodedDataLength { get; }
    public double Timestamp { get; }
    internal NetworkLoadingFinishedEventArgs(string requestId, double encodedDataLength, double timestamp)
    { RequestId = requestId; EncodedDataLength = encodedDataLength; Timestamp = timestamp; }
}

/// <summary>Args for <see cref="NetworkClient.LoadingFailed"/>.</summary>
public sealed class NetworkLoadingFailedEventArgs : EventArgs
{
    public string RequestId { get; }
    public string ErrorText { get; }
    public bool Canceled { get; }
    /// <summary>Non-empty if blocked by Chromium (e.g. "mixed-content", "subresource-filter").</summary>
    public string BlockedReason { get; }
    internal NetworkLoadingFailedEventArgs(string requestId, string errorText, bool canceled, string blockedReason)
    { RequestId = requestId; ErrorText = errorText; Canceled = canceled; BlockedReason = blockedReason; }
}

/// <summary>
/// Returned by <see cref="NetworkClient.GetResponseBodyAsync"/>.
/// </summary>
public sealed record NetworkResponseBody(string Body, bool Base64Encoded)
{
    /// <summary>Decode <see cref="Body"/> to raw bytes, handling the base64 flag.</summary>
    public byte[] AsBytes() => Base64Encoded
        ? Convert.FromBase64String(Body)
        : System.Text.Encoding.UTF8.GetBytes(Body);

    /// <summary>Decode <see cref="Body"/> as text, handling the base64 flag (treats base64 payload as UTF-8 bytes).</summary>
    public string AsText() => Base64Encoded
        ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Body))
        : Body;
}
