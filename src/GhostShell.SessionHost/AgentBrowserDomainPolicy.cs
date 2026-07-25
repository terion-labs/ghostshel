using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

/// <summary>
/// Applies the first browser slice's host-owned origin policy after a
/// one-action authorization has been consumed and again immediately before
/// renderer dispatch.
/// </summary>
internal static class AgentBrowserDomainPolicy
{
    public const string DeniedStableCode = "browser_domain_policy_denied";
    public const string NavigationInProgressStableCode =
        "navigation_in_progress";
    public const string BrowserStateChangedStableCode =
        "browser_state_changed";

    public static AgentBrowserDomainPolicyDecision Evaluate(
        AgentBrowserRequest request,
        BrowserSessionState currentState,
        AgentAuthorizationSource authorizationSource)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentState);

        if (authorizationSource == AgentAuthorizationSource.YoloPolicy
            || (authorizationSource == AgentAuthorizationSource.AutoPolicy
                && !IsAllowedAutomatically(request, currentState))
            || authorizationSource is not (
                AgentAuthorizationSource.HumanApproval
                or AgentAuthorizationSource.AutoPolicy))
        {
            return AgentBrowserDomainPolicyDecision.Deny(Denied());
        }

        if (RequiresOriginGuard(request)
            && currentState.LoadState == BrowserLoadState.Loading)
        {
            return AgentBrowserDomainPolicyDecision.Deny(
                new HostError(
                    HostErrorCode.InvalidRequest,
                    NavigationInProgressStableCode,
                    "The browser is already loading a page.",
                    Retryable: true));
        }

        if (request is AgentBrowserRequest.Snapshot
            && currentState.LoadState != BrowserLoadState.Ready)
        {
            return AgentBrowserDomainPolicyDecision.Deny(
                new HostError(
                    HostErrorCode.InvalidRequest,
                    NavigationInProgressStableCode,
                    "The browser document is not ready for snapshot capture.",
                    Retryable: true));
        }

        if (request is AgentBrowserRequest.Click
            or AgentBrowserRequest.Fill
            or AgentBrowserRequest.Check)
        {
            if (currentState.LoadState != BrowserLoadState.Ready)
            {
                return AgentBrowserDomainPolicyDecision.Deny(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        NavigationInProgressStableCode,
                        "The browser document is not ready for interaction.",
                        Retryable: true));
            }

            var requestedDocumentRevision = request switch
            {
                AgentBrowserRequest.Click click =>
                    click.Value.DocumentRevision,
                AgentBrowserRequest.Fill fill =>
                    fill.Value.DocumentRevision,
                AgentBrowserRequest.Check check =>
                    check.Value.DocumentRevision,
                _ => throw new InvalidOperationException(
                    "The browser interaction kind is unsupported."),
            };
            if (requestedDocumentRevision != currentState.DocumentRevision)
            {
                return AgentBrowserDomainPolicyDecision.Deny(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        BrowserStateChangedStableCode,
                        "The browser document changed after the element was observed.",
                        Retryable: true));
            }
        }

        var allowedOrigin = OriginFor(request, currentState);
        return AgentBrowserDomainPolicyDecision.Allow(
            allowedOrigin,
            allowedOrigin is null
                ? null
                : BrowserNavigationStartBinding.FromState(currentState));
    }

    private static bool IsAllowedAutomatically(
        AgentBrowserRequest request,
        BrowserSessionState currentState) =>
        request switch
        {
            AgentBrowserRequest.ReadState => true,
            AgentBrowserRequest.Snapshot => true,
            AgentBrowserRequest.Click => false,
            AgentBrowserRequest.Fill => false,
            AgentBrowserRequest.Check => false,
            AgentBrowserRequest.Navigate navigate =>
                HasSameOrigin(
                    currentState.Address,
                    navigate.Value.Address),
            AgentBrowserRequest.Reload => true,
            AgentBrowserRequest.Stop => true,
            AgentBrowserRequest.Back => false,
            AgentBrowserRequest.Forward => false,
            _ => false,
        };

    private static bool HasSameOrigin(
        BrowserAddress current,
        BrowserAddress destination) =>
        BrowserNavigationOrigin
            .FromAddress(current)
            .Allows(destination);

    private static BrowserNavigationOrigin? OriginFor(
        AgentBrowserRequest request,
        BrowserSessionState currentState) =>
        request switch
        {
            AgentBrowserRequest.Navigate navigate =>
                BrowserNavigationOrigin.FromAddress(
                    navigate.Value.Address),
            AgentBrowserRequest.Click =>
                BrowserNavigationOrigin.FromAddress(
                    currentState.Address),
            AgentBrowserRequest.Fill =>
                BrowserNavigationOrigin.FromAddress(
                    currentState.Address),
            AgentBrowserRequest.Check =>
                BrowserNavigationOrigin.FromAddress(
                    currentState.Address),
            AgentBrowserRequest.Back
                or AgentBrowserRequest.Forward
                or AgentBrowserRequest.Reload =>
                BrowserNavigationOrigin.FromAddress(
                    currentState.Address),
            AgentBrowserRequest.ReadState
                or AgentBrowserRequest.Snapshot
                or AgentBrowserRequest.Stop => null,
            _ => null,
        };

    private static bool RequiresOriginGuard(AgentBrowserRequest request) =>
        request is AgentBrowserRequest.Navigate
            or AgentBrowserRequest.Click
            or AgentBrowserRequest.Fill
            or AgentBrowserRequest.Check
            or AgentBrowserRequest.Back
            or AgentBrowserRequest.Forward
            or AgentBrowserRequest.Reload;

    private static HostError Denied() =>
        new(
            HostErrorCode.InvalidRequest,
            DeniedStableCode,
            "The governed browser action is outside the host navigation policy.");
}

internal sealed record AgentBrowserDomainPolicyDecision
{
    private AgentBrowserDomainPolicyDecision(
        BrowserNavigationOrigin? allowedOrigin,
        BrowserNavigationStartBinding? startBinding,
        HostError? error)
    {
        if ((allowedOrigin is not null || startBinding is not null)
            && error is not null)
        {
            throw new ArgumentException(
                "A denied browser policy decision cannot carry a navigation binding.");
        }

        if ((allowedOrigin is null) != (startBinding is null))
        {
            throw new ArgumentException(
                "A governed browser policy decision must carry both its origin and starting document.");
        }

        AllowedOrigin = allowedOrigin;
        StartBinding = startBinding;
        Error = error;
    }

    public BrowserNavigationOrigin? AllowedOrigin { get; }

    public BrowserNavigationStartBinding? StartBinding { get; }

    public HostError? Error { get; }

    public bool IsAllowed => Error is null;

    public static AgentBrowserDomainPolicyDecision Allow(
        BrowserNavigationOrigin? allowedOrigin,
        BrowserNavigationStartBinding? startBinding) =>
        new(allowedOrigin, startBinding, error: null);

    public static AgentBrowserDomainPolicyDecision Deny(HostError error) =>
        new(
            allowedOrigin: null,
            startBinding: null,
            error: error ?? throw new ArgumentNullException(nameof(error)));
}
