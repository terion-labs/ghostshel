using System.Text.Json;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Runs one anonymous Google search in a detached CEF renderer. The only
/// script dispatched to the page is the fixed extractor owned by this assembly.
/// </summary>
public sealed class CefAgentWebSearchExecutor : IAgentWebSearchExecutor
{
    private static readonly TimeSpan SearchDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan NetworkQuietWindow =
        TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan NetworkObservationPollInterval =
        TimeSpan.FromMilliseconds(25);
    private static readonly SemaphoreSlim SearchGate = new(1, 1);

    public async ValueTask<AgentWebSearchExecutionResult> SearchAsync(
        AgentWebSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Failed(AgentWebSearchErrorCode.Cancelled);
        }

        try
        {
            await SearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failed(AgentWebSearchErrorCode.Cancelled);
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            deadline.CancelAfter(SearchDeadline);
            return await SearchCoreAsync(request, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failed(
                cancellationToken.IsCancellationRequested
                    ? AgentWebSearchErrorCode.Cancelled
                    : AgentWebSearchErrorCode.TimedOut);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return Failed(AgentWebSearchErrorCode.Unavailable);
        }
        finally
        {
            SearchGate.Release();
        }
    }

    internal static BrowserAddress CreateSearchAddress(
        AgentWebSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var encoded = Uri.EscapeDataString(request.Query);
        var value = new Uri(
            $"https://www.google.com/search?q={encoded}&num={request.ResultCount}&hl=en&pws=0",
            UriKind.Absolute);
        return new BrowserAddress(value);
    }

    internal static AgentWebSearchExecutionResult ParseExtraction(
        BrowserAddress finalAddress,
        string json)
    {
        ArgumentNullException.ThrowIfNull(finalAddress);
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            var title = root.GetProperty("title").GetString() ?? string.Empty;
            var pageText = root.GetProperty("pageText").GetString() ?? string.Empty;
            var truncated = root.GetProperty("truncated").GetBoolean();
            if (IsInterstitial(finalAddress.Value, title, pageText))
            {
                return Failed(AgentWebSearchErrorCode.Interstitial);
            }

            var parsed = ParseEntries(root.GetProperty("results"));
            if (parsed.Entries.Count == 0)
            {
                return Failed(AgentWebSearchErrorCode.ExtractionFailed);
            }

            truncated |= parsed.Truncated;
            return new AgentWebSearchExecutionResult.Succeeded(
                new AgentWebSearchResult(
                    finalAddress.Value.AbsoluteUri,
                    title,
                    parsed.Entries,
                    truncated));
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or InvalidOperationException
            or JsonException
            or KeyNotFoundException)
        {
            return Failed(AgentWebSearchErrorCode.ExtractionFailed);
        }
    }

    private static SearchEntries ParseEntries(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Search results must be an array.");
        }

        List<AgentWebSearchEntry> entries = [];
        HashSet<string> seenUrls = new(StringComparer.Ordinal);
        var truncated = false;
        foreach (var candidate in value.EnumerateArray())
        {
            if (entries.Count >= AgentWebSearchRequest.MaximumResultCount)
            {
                truncated = true;
                break;
            }

            if (!TryCreateEntry(candidate, out var entry, out var entryTruncated)
                || !seenUrls.Add(entry.Url))
            {
                continue;
            }

            truncated |= entryTruncated;
            entries.Add(entry);
        }

        return new SearchEntries(entries, truncated);
    }

    private static bool TryCreateEntry(
        JsonElement candidate,
        out AgentWebSearchEntry entry,
        out bool truncated)
    {
        entry = null!;
        truncated = false;
        if (candidate.ValueKind != JsonValueKind.Object
            || !candidate.TryGetProperty("url", out var urlValue)
            || urlValue.ValueKind != JsonValueKind.String
            || !candidate.TryGetProperty("desc", out var descriptionValue)
            || descriptionValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var url = urlValue.GetString();
        var description = descriptionValue.GetString();
        if (url is null || description is null)
        {
            return false;
        }

        description = BoundedWebText.Truncate(
            description.Trim(),
            AgentWebSearchEntry.MaximumDescriptionBytes,
            out truncated);
        try
        {
            entry = new AgentWebSearchEntry(url, description);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async ValueTask<AgentWebSearchExecutionResult> SearchCoreAsync(
        AgentWebSearchRequest request,
        CancellationToken cancellationToken)
    {
        CefBrowserNetworkContext? network = null;
        CefBrowserView? browser = null;
        try
        {
            (network, browser) = await AvaloniaBrowserUiDispatcher.Instance
                .InvokeAsync(() =>
                {
                    var createdNetwork =
                        CefBrowserNetworkContext.CreateIsolatedAgentWeb();
                    var createdBrowser = createdNetwork.CreateView();
                    createdBrowser.SetResourceRequestPolicy(
                        (candidate, token) => BrowserDestinationPolicy.LocalSystem
                            .AllowsResolvedAsync(candidate, token));
                    return (createdNetwork, createdBrowser);
                });
            if (!await browser.BeginNetworkActivityObservationWhenReadyAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return Failed(AgentWebSearchErrorCode.Unavailable);
            }

            await WaitForObservableNetworkAsync(browser, cancellationToken)
                .ConfigureAwait(false);
            var navigation = new TaskCompletionSource<NavigationOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await BeginNavigationAsync(
                    browser,
                    CreateSearchAddress(request),
                    navigation,
                    cancellationToken)
                .ConfigureAwait(false);
            var outcome = await navigation.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.IsSuccess || outcome.Address is null)
            {
                return Failed(outcome.ErrorCode);
            }

            await WaitForNetworkSettlementAsync(browser, cancellationToken)
                .ConfigureAwait(false);
            var extracted = await browser
                .ExtractWebSearchDocumentAsync(request.ResultCount)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (extracted.Status != NativeBrowserAutomationStatus.Acknowledged
                || extracted.ResultJson is null)
            {
                return Failed(AgentWebSearchErrorCode.ExtractionFailed);
            }

            return ParseExtraction(outcome.Address, extracted.ResultJson);
        }
        finally
        {
            if (browser is not null || network is not null)
            {
                await AvaloniaBrowserUiDispatcher.Instance.InvokeAsync(() =>
                {
                    browser?.EndNetworkActivityObservation();
                    browser?.Dispose();
                    network?.Dispose();
                    return true;
                });
            }
        }
    }

    private static async Task WaitForObservableNetworkAsync(
        CefBrowserView browser,
        CancellationToken cancellationToken)
    {
        while (!browser.ReadNetworkActivity().IsObservable)
        {
            await Task.Delay(NetworkObservationPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WaitForNetworkSettlementAsync(
        CefBrowserView browser,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var activity = browser.ReadNetworkActivity();
            if (activity.IsObservable
                && activity.ActiveRequestCount == 0
                && activity.QuietFor >= NetworkQuietWindow)
            {
                return;
            }

            await Task.Delay(NetworkObservationPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask BeginNavigationAsync(
        CefBrowserView browser,
        BrowserAddress address,
        TaskCompletionSource<NavigationOutcome> completion,
        CancellationToken cancellationToken)
    {
        await AvaloniaBrowserUiDispatcher.Instance.InvokeAsync(() =>
        {
            browser.NavigationStarted += (_, args) =>
            {
                if (!IsGoogleAddress(args.Address.Value))
                {
                    args.Cancel = true;
                    return;
                }

                try
                {
                    browser.SetActiveNavigationRequestPolicy(
                        (candidate, token) => IsGoogleAddress(candidate.Value)
                            ? BrowserDestinationPolicy.LocalSystem
                                .AllowsResolvedAsync(candidate, token)
                            : ValueTask.FromResult(false));
                }
                catch (InvalidOperationException)
                {
                    args.Cancel = true;
                }
            };
            browser.NavigationRejected += (_, _) =>
                completion.TrySetResult(NavigationOutcome.Denied());
            browser.RenderProcessFailed += (_, _) =>
                completion.TrySetResult(NavigationOutcome.Unavailable());
            browser.NavigationCompleted += (_, args) =>
                completion.TrySetResult(
                    args.IsSuccess && args.Address is not null
                        ? NavigationOutcome.Succeeded(args.Address)
                        : NavigationOutcome.LoadFailed());
            browser.Navigate(address);
            return true;
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsGoogleAddress(Uri address)
    {
        if (!address.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = address.IdnHost.TrimEnd('.');
        return host.Equals("google.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInterstitial(Uri address, string title, string text) =>
        address.AbsolutePath.StartsWith("/sorry", StringComparison.OrdinalIgnoreCase)
        || address.IdnHost.Equals(
            "consent.google.com",
            StringComparison.OrdinalIgnoreCase)
        || title.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)
        || text.Contains(
            "before you continue to google",
            StringComparison.OrdinalIgnoreCase);

    private static AgentWebSearchExecutionResult Failed(
        AgentWebSearchErrorCode code) =>
        new AgentWebSearchExecutionResult.Failed(code);

    private sealed record SearchEntries(
        IReadOnlyList<AgentWebSearchEntry> Entries,
        bool Truncated);

    private sealed record NavigationOutcome(
        bool IsSuccess,
        BrowserAddress? Address,
        AgentWebSearchErrorCode ErrorCode)
    {
        public static NavigationOutcome Succeeded(BrowserAddress address) =>
            new(true, address, AgentWebSearchErrorCode.LoadFailed);

        public static NavigationOutcome Denied() =>
            new(false, null, AgentWebSearchErrorCode.NavigationDenied);

        public static NavigationOutcome LoadFailed() =>
            new(false, null, AgentWebSearchErrorCode.LoadFailed);

        public static NavigationOutcome Unavailable() =>
            new(false, null, AgentWebSearchErrorCode.Unavailable);
    }
}
