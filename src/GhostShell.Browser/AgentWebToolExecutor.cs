using GhostShell.Application;

namespace GhostShell.Browser;

public sealed class AgentWebToolExecutor : IAgentWebToolExecutor
{
    private readonly PeerBoundHttpFetcher _httpFetcher = new();
    private readonly CefAgentWebReader _webReader = new();
    private readonly CefAgentWebSearchExecutor _webSearch = new();

    public async ValueTask<AgentWebToolExecutionResult> ExecuteAsync(
        AgentWebToolRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request switch
        {
            AgentHttpFetchRequest fetch =>
                await _httpFetcher.FetchAsync(fetch, cancellationToken)
                    .ConfigureAwait(false),
            AgentWebReadRequest read =>
                await _webReader.ReadAsync(read, cancellationToken)
                    .ConfigureAwait(false),
            AgentWebSearchRequest search =>
                Map(await _webSearch.SearchAsync(search, cancellationToken)
                    .ConfigureAwait(false)),
            _ => new AgentWebToolExecutionResult.Failed(
                AgentWebToolErrorCode.Unavailable),
        };
    }

    private static AgentWebToolExecutionResult Map(
        AgentWebSearchExecutionResult result) => result switch
        {
            AgentWebSearchExecutionResult.Succeeded succeeded =>
                new AgentWebToolExecutionResult.Succeeded(succeeded.Result),
            AgentWebSearchExecutionResult.Failed failed =>
                new AgentWebToolExecutionResult.Failed(Map(failed.Code)),
            _ => new AgentWebToolExecutionResult.Failed(AgentWebToolErrorCode.Unavailable),
        };

    private static AgentWebToolErrorCode Map(AgentWebSearchErrorCode code) => code switch
    {
        AgentWebSearchErrorCode.NavigationDenied => AgentWebToolErrorCode.DestinationDenied,
        AgentWebSearchErrorCode.LoadFailed => AgentWebToolErrorCode.LoadFailed,
        AgentWebSearchErrorCode.Interstitial => AgentWebToolErrorCode.SearchInterstitial,
        AgentWebSearchErrorCode.ExtractionFailed => AgentWebToolErrorCode.ExtractionFailed,
        AgentWebSearchErrorCode.TimedOut => AgentWebToolErrorCode.TimedOut,
        AgentWebSearchErrorCode.Cancelled => AgentWebToolErrorCode.Cancelled,
        _ => AgentWebToolErrorCode.Unavailable,
    };
}
