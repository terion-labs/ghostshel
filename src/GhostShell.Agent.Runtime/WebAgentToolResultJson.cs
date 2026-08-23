using System.Buffers;
using System.Text;
using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

internal static class WebAgentToolResultJson
{
    public static WebAgentToolJsonProjection Project(
        AgentWebToolRequest request,
        AgentWebToolResult result)
    {
        if (request is AgentWebSearchRequest searchRequest
            && result is AgentWebSearchResult searchResult)
        {
            var projected = WebSearchAgentToolResultJson.Project(
                searchRequest,
                searchResult);
            return new WebAgentToolJsonProjection(
                projected.IsSuccess,
                projected.StableCode,
                projected.Json);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString("content_origin", WebSearchAgentToolResultJson.ContentOrigin);
        switch (request, result)
        {
            case (AgentHttpFetchRequest fetch, AgentHttpFetchResult fetched):
                writer.WriteString("method", fetch.Method.ToString().ToUpperInvariant());
                writer.WriteString("final_url", fetched.FinalUrl);
                writer.WriteNumber("status", fetched.StatusCode);
                writer.WriteString("media_type", fetched.MediaType);
                writer.WriteString("content", fetched.Content);
                break;
            case (AgentWebReadRequest read, AgentWebReadResult readResult):
                writer.WriteString("final_url", readResult.FinalUrl);
                writer.WriteString("title", readResult.Title);
                writer.WriteString(
                    "format",
                    read.Format is AgentWebReadFormat.Markdown
                        ? "markdown"
                        : "rendered_html");
                writer.WriteString("content", readResult.Content);
                writer.WriteBoolean("truncated", readResult.Truncated);
                break;
            default:
                return Rejected("web_result_type_mismatch");
        }

        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("web_result_too_large");
        }

        return new WebAgentToolJsonProjection(
            true,
            CompletedCode(request.ToolName),
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (IsProviderStableCode(error.StableCode)
            || string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal))
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.DeadlineExceeded => "web_timed_out",
            HostErrorCode.Cancelled => "web_cancelled",
            HostErrorCode.InvalidRequest or HostErrorCode.RevisionConflict => "target_changed",
            _ => "web_failed",
        };
    }

    public static string Failure(HostError error) =>
        AgentToolResultJson.Failure(ProviderStableCode(error), error.Retryable);

    public static string CompletedCode(string toolName) => toolName switch
    {
        BuiltInAgentTools.HttpFetch => "http_fetch_completed",
        BuiltInAgentTools.WebRead => "web_read_completed",
        BuiltInAgentTools.WebSearch => "web_search_completed",
        _ => "web_completed",
    };

    private static bool IsProviderStableCode(string stableCode)
    {
        var prefixLength = stableCode.StartsWith(
            "http_fetch_",
            StringComparison.Ordinal)
            ? "http_fetch_".Length
            : stableCode.StartsWith("web_read_", StringComparison.Ordinal)
                ? "web_read_".Length
                : stableCode.StartsWith("web_search_", StringComparison.Ordinal)
                    ? "web_search_".Length
                    : 0;
        if (prefixLength == 0)
        {
            return false;
        }

        return stableCode[prefixLength..] is
            "invalid_url"
            or "destination_denied"
            or "dns_failed"
            or "redirect_limit"
            or "timed_out"
            or "body_too_large"
            or "unsupported_content_type"
            or "load_failed"
            or "render_process_failed"
            or "extraction_failed"
            or "converter_failed"
            or "interstitial"
            or "cancelled"
            or "unavailable";
    }

    private static WebAgentToolJsonProjection Rejected(string stableCode) =>
        new(false, stableCode, AgentToolResultJson.Failure(stableCode, retryable: false));
}

internal sealed record WebAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
