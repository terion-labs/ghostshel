using GhostShell.Application;
using GhostShell.Core;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class BrowserOperationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureRejectsBrowserSessionCapabilityProfileMismatch(
        bool createdSessionIsWider)
    {
        var advertisedCapabilities = BrowserCapabilities();
        var createdCapabilities = createdSessionIsWider
            ? new CapabilitySet(
            [
                .. advertisedCapabilities.Values,
                SessionCapabilities.BrowserSnapshot,
            ])
            : new CapabilitySet(
                advertisedCapabilities.Values.Where(
                    capability =>
                        capability != SessionCapabilities.BrowserStop));
        var browsers = new FakeBrowserPanelSessionFactory(
            advertisedCapabilities,
            createdCapabilities);
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("capability-mismatch");

        var rejected = await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("mismatched-panel"),
                "Browser",
                Address("https://example.test/")),
            Context(),
            CancellationToken.None);
        var notRegistered = await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.EngineFailed, rejected.Error().Code);
        Assert.Equal("engine_failed", rejected.Error().StableCode);
        Assert.False(rejected.Error().Retryable);
        Assert.Equal(1, browsers[sessionId].DisposeCount);
        Assert.Equal(HostErrorCode.NotFound, notRegistered.Error().Code);
    }

    [Fact]
    public async Task EnsureRegistersBrowserSessionWhenCapabilityProfilesMatch()
    {
        var capabilities = BrowserCapabilities();
        var browsers = new FakeBrowserPanelSessionFactory(
            capabilities,
            capabilities);
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("matching-capabilities");

        var opened = await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("matching-panel"),
                "Browser",
                Address("https://example.test/")),
            Context(),
            CancellationToken.None);

        Assert.IsType<HostResult<SessionSnapshot>.Success>(opened);
        Assert.Equal(0, browsers[sessionId].DisposeCount);
    }

    [Fact]
    public async Task HostOwnsBrowserLifecycleAndDispatchesTypedNavigation()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var initialAddress = Address("https://example.test/");
        var opened = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureBrowserSessionAsync(
                new EnsureBrowserSessionRequest(
                    sessionId,
                    Owner("panel-1"),
                    "Browser",
                    initialAddress),
                Context(),
                CancellationToken.None));

        var hello = (await host.NegotiateAsync(
            new ClientHello([1], BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.BrowserNavigate));

        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(1024, 768, 1),
                BrowserCapabilities()),
            Context(expectedRevision: opened.ResultingRevision),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(initialAddress);
        var attached = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None);

        Assert.IsType<HostResult<Unit>.Success>(attached);
        Assert.Equal(1, browsers[sessionId].AttachCount);

        var destination = Address("https://docs.example.test/guide");
        var idempotency = new IdempotencyKey("browser-navigation-1");
        var navigationContext = Context(idempotencyKey: idempotency);
        var navigated = Assert.IsType<
            HostResult<BrowserResult<BrowserSessionState>>.Success>(
            await host.NavigateBrowserAsync(
                new BrowserNavigateRequest(sessionId, destination),
                navigationContext,
                CancellationToken.None));
        var replayed = Assert.IsType<
            HostResult<BrowserResult<BrowserSessionState>>.Success>(
            await host.NavigateBrowserAsync(
                new BrowserNavigateRequest(sessionId, destination),
                navigationContext,
                CancellationToken.None));

        Assert.True(navigated.Value.IsSuccess, navigated.Value.Error?.Message);
        Assert.Equal(destination, navigated.Value.Value?.Address);
        Assert.Equal(navigated.ResultingRevision, replayed.ResultingRevision);
        Assert.Equal(1, renderer.NavigateCount);

        Assert.True((await host.GoBackBrowserAsync(
            sessionId,
            Context(),
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.GoForwardBrowserAsync(
            sessionId,
            Context(),
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.ReloadBrowserAsync(
            sessionId,
            Context(),
            CancellationToken.None)).Value().IsSuccess);
        Assert.True((await host.StopBrowserAsync(
            sessionId,
            Context(),
            CancellationToken.None)).Value().IsSuccess);
        var state = (await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None)).Value();

        Assert.Equal(1, renderer.BackCount);
        Assert.Equal(1, renderer.ForwardCount);
        Assert.Equal(1, renderer.ReloadCount);
        Assert.Equal(1, renderer.StopCount);
        Assert.Equal(destination, state.Value?.Address);

        var detached = await host.DetachAsync(
            new DetachSessionRequest(attachment.Attachment.Id, sessionId),
            Context(),
            CancellationToken.None);
        var unavailable = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, initialAddress),
            Context(),
            CancellationToken.None);

        Assert.IsType<HostResult<Unit>.Success>(detached);
        Assert.Equal(1, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.LeaseDenied, unavailable.Error().Code);
    }

    [Fact]
    public async Task RendererAttachmentRequiresTheExactInteractiveHumanClient()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("panel-1"),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();

        var wrongClient = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(new ClientId("other-client")),
            CancellationToken.None);
        var wrongAttachment = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                new AttachmentId("missing-attachment"),
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.LeaseDenied, wrongClient.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, wrongAttachment.Error().Code);
        Assert.Equal(0, browsers[sessionId].AttachCount);
    }

    [Fact]
    public async Task ReplacingOrDisconnectingInteractiveClientDetachesBrowserRenderer()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("panel-1"),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var firstAttachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                firstAttachment.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None)).Value();

        var replacement = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(1200, 800, 2),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var staleAttachment = await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                firstAttachment.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                replacement.Attachment.Id,
                new FakeBrowserRenderer(address)),
            Context(),
            CancellationToken.None)).Value();

        Assert.Equal(1, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.LeaseDenied, staleAttachment.Error().Code);
        Assert.Equal(2, browsers[sessionId].AttachCount);

        _ = (await host.DisconnectClientAsync(
            ClientId,
            Context(),
            CancellationToken.None)).Value();
        var unavailable = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, address),
            Context(),
            CancellationToken.None);

        Assert.Equal(2, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.LeaseDenied, unavailable.Error().Code);
    }

    [Fact]
    public async Task NormalBrowserOperationsRequireTheCurrentInteractiveHumanPrincipal()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner("panel-1"),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(address);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None)).Value();

        var agentContext = new OperationContext(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("agent-1"),
                ActorKind.Agent,
                "Agent",
                ClientId),
            CancellationId: CancellationId.New());
        var agentResults = new[]
        {
            await host.ReadBrowserStateAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.NavigateBrowserAsync(
                new BrowserNavigateRequest(
                    sessionId,
                    Address("https://blocked.example.test/")),
                agentContext,
                CancellationToken.None),
            await host.GoBackBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.GoForwardBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.ReloadBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
            await host.StopBrowserAsync(
                sessionId,
                agentContext,
                CancellationToken.None),
        };

        Assert.All(
            agentResults,
            result => Assert.Equal(HostErrorCode.LeaseDenied, result.Error().Code));
        Assert.Equal(0, renderer.NavigateCount);
        Assert.Equal(0, renderer.BackCount);
        Assert.Equal(0, renderer.ForwardCount);
        Assert.Equal(0, renderer.ReloadCount);
        Assert.Equal(0, renderer.StopCount);

        var mismatchedHuman = new OperationContext(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("different-id"),
                ActorKind.Human,
                "Impersonated user",
                ClientId),
            CancellationId: CancellationId.New());
        var mismatched = await host.ReadBrowserStateAsync(
            sessionId,
            mismatchedHuman,
            CancellationToken.None);
        Assert.Equal(HostErrorCode.LeaseDenied, mismatched.Error().Code);

        _ = (await host.DetachAsync(
            new DetachSessionRequest(attachment.Attachment.Id, sessionId),
            Context(),
            CancellationToken.None)).Value();
        var detached = await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.LeaseDenied, detached.Error().Code);
    }

    [Fact]
    public async Task ContextGuardsRunBeforeBrowserDispatchAndCloseDisposesTheRenderer()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers, clock);
        var sessionId = new SessionId("browser-1");
        var address = Address("https://example.test/");
        var opened = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureBrowserSessionAsync(
                new EnsureBrowserSessionRequest(
                    sessionId,
                    Owner("panel-1"),
                    "Browser",
                    address),
                Context(),
                CancellationToken.None));
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(address);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None)).Value();

        var stale = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, Address("https://stale.example.test/")),
            Context(expectedRevision: opened.ResultingRevision),
            CancellationToken.None);
        var expired = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, Address("https://expired.example.test/")),
            Context(deadline: clock.GetUtcNow()),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await host.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, Address("https://cancelled.example.test/")),
            Context(),
            cancellation.Token);

        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);
        Assert.Equal(HostErrorCode.DeadlineExceeded, expired.Error().Code);
        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.Equal(0, renderer.NavigateCount);

        var closed = (await host.CloseAsync(
            CloseScopeRequest.Panel(
                new PanelInstanceId("panel-1"),
                CloseDecision.Request),
            Context(),
            CancellationToken.None)).Value();
        var afterClose = await host.ReadBrowserStateAsync(
            sessionId,
            Context(),
            CancellationToken.None);

        Assert.IsType<CloseScopeResult.Completed>(closed);
        Assert.Equal(1, browsers[sessionId].DetachCount);
        Assert.Equal(HostErrorCode.SessionClosed, afterClose.Error().Code);
    }

    [Fact]
    public async Task LoadingBrowserClosesWithoutDestructiveConfirmation()
    {
        var browsers = new FakeBrowserPanelSessionFactory();
        await using var host = CreateHost(browsers);
        var sessionId = new SessionId("loading-browser");
        var panelId = new PanelInstanceId("loading-browser-panel");
        var address = Address("https://example.test/watch?v=one");
        _ = (await host.EnsureBrowserSessionAsync(
            new EnsureBrowserSessionRequest(
                sessionId,
                Owner(panelId.Value),
                "Browser",
                address),
            Context(),
            CancellationToken.None)).Value();
        var attachment = (await host.AttachAsync(
            new AttachSessionRequest(
                sessionId,
                ClientId,
                AttachmentKind.Interactive,
                new ViewportDescriptor(800, 600, 1),
                BrowserCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var renderer = new FakeBrowserRenderer(address);
        _ = (await host.AttachBrowserRendererAsync(
            new AttachBrowserRendererRequest(
                sessionId,
                attachment.Attachment.Id,
                renderer),
            Context(),
            CancellationToken.None)).Value();
        renderer.BeginLoading(address);

        var closed = (await host.CloseAsync(
            CloseScopeRequest.Panel(panelId, CloseDecision.Request),
            Context(),
            CancellationToken.None)).Value();

        var completed = Assert.IsType<CloseScopeResult.Completed>(closed);
        Assert.Equal(
            SessionCloseOutcome.GracefullyClosed,
            Assert.Single(completed.Sessions).Outcome);
    }

    private static readonly ClientId ClientId = new("client-1");

    private static InMemorySessionHostClient CreateHost(
        IBrowserPanelSessionFactory browserFactory,
        TimeProvider? timeProvider = null) => new(
        new FakeTerminalSessionFactory(),
        new DesktopLifecyclePolicy(),
        timeProvider ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch),
        browserPanelFactory: browserFactory);

    private static BrowserAddress Address(string value) =>
        new(new Uri(value, UriKind.Absolute));

    private static OperationContext Context(
        ClientId? clientId = null,
        long? expectedRevision = null,
        IdempotencyKey? idempotencyKey = null,
        DateTimeOffset? deadline = null)
    {
        var actualClientId = clientId ?? ClientId;
        return new OperationContext(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId(actualClientId.Value),
                ActorKind.Human,
                "Test user",
                actualClientId),
            expectedRevision,
            idempotencyKey,
            CancellationId.New(),
            deadline);
    }

    private static SessionOwner Owner(string panelId) => new(
        HostMode.Desktop,
        new WindowInstanceId("window-1"),
        new WorkspaceInstanceId("workspace-1"),
        new TabInstanceId("tab-1"),
        new PanelInstanceId(panelId));

    private static CapabilitySet BrowserCapabilities() => new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.AttachInteractive,
        SessionCapabilities.BrowserReadState,
        SessionCapabilities.BrowserNavigate,
        SessionCapabilities.BrowserBack,
        SessionCapabilities.BrowserForward,
        SessionCapabilities.BrowserReload,
        SessionCapabilities.BrowserStop,
    ]);
}
