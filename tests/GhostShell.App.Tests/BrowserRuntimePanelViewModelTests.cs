using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using GhostShell.App;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class BrowserRuntimePanelViewModelTests
{
    [Fact]
    public void Blank_browser_address_starts_as_empty_editor_text()
    {
        var browser = new BrowserPresentationHost();

        Assert.Empty(browser.AddressText);
    }

    /// <summary>
    /// Hiding a native surface must never give it up.
    ///
    /// A native control does not survive leaving the visual tree — the framework
    /// destroys it and builds a fresh one on the way back — so a surface that was
    /// removed whenever its panel went off screen lost the page it was showing.
    /// That is what adding a panel beside a browser used to do: rearranging
    /// panels rebuilds the views that draw them, and the document went with the
    /// view. Concealing keeps the surface parented; only the panel's own end
    /// releases it.
    /// </summary>
    [Fact]
    public void Concealing_a_native_surface_keeps_it_parented_and_only_release_gives_it_up()
    {
        var layer = new NativeSurfaceLayer();
        var surface = new Border();

        layer.Present(surface, new Rect(10, 20, 300, 200));
        Assert.Contains(surface, layer.Children);
        Assert.True(surface.IsVisible);
        Assert.Equal(10, Canvas.GetLeft(surface));
        Assert.Equal(20, Canvas.GetTop(surface));
        Assert.Equal(300, surface.Width);
        Assert.Equal(200, surface.Height);

        layer.Conceal(surface);
        Assert.Contains(surface, layer.Children);
        Assert.False(surface.IsVisible);

        // And showing it again is a move, not a rebuild.
        layer.Present(surface, new Rect(0, 0, 120, 90));
        Assert.Single(layer.Children);
        Assert.True(surface.IsVisible);

        layer.Release(surface);
        Assert.DoesNotContain(surface, layer.Children);
    }

    /// <summary>
    /// The panel owns the surface and the attachment, so ending the panel is what
    /// ends both — not whichever control happened to be drawing it.
    /// </summary>
    [Fact]
    public void Disposing_the_renderer_view_releases_its_surface_from_the_layer()
    {
        var layer = new NativeSurfaceLayer();
        var view = new Border();
        var rendererView = new BrowserRendererView(view, new RecordingBrowserRenderer());

        layer.Present(view, new Rect(0, 0, 100, 100));
        rendererView.Layer = layer;

        rendererView.Dispose();

        Assert.DoesNotContain(view, layer.Children);
        Assert.Null(rendererView.Attachment);
    }

    [Fact]
    public void BrowserStateFlowsIntoRecoveryAndRendererLifetimeIsReleasedOnce()
    {
        var lifetime = new RecordingLifetime();
        var renderer = new RecordingBrowserRenderer();
        var panel = new BrowserRuntimePanelViewModel(
            new PanelInstanceId("browser-panel"),
            "Documentation",
            new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("browser-panel")),
            BrowserAddress.Blank,
            DispatchProxy.Create<ISessionHostClient, NoopSessionClient>(),
            new ClientId("client"),
            new BrowserRendererView(new Border(), renderer, lifetime));
        var address = Address("https://docs.example.test/guide");
        panel.ApplyBrowserState(new BrowserSessionState(
            address,
            "Guide",
            BrowserLoadState.Ready,
            canGoBack: true,
            canGoForward: false,
            documentRevision: 7));
        var tab = new RuntimeTabViewModel(
            new TabInstanceId("tab"),
            "Docs",
            "TEST");
        tab.AddPanel(panel);
        var workspace = new RuntimeWorkspaceViewModel(
            new WorkspaceInstanceId("workspace"),
            "Workspace",
            "#123456",
            []);
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;

        var json = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);
        var deserialized = RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            new RuntimeRecoverySnapshot(
                "run",
                RuntimeWorkspaceRecoveryCodec.SnapshotKey,
                RuntimeWorkspaceRecoveryCodec.SchemaVersion,
                json,
                DateTimeOffset.UnixEpoch),
            out var recovery,
            out var error);

        Assert.True(deserialized, error);
        var recoveredPanel = Assert.Single(
            Assert.Single(recovery!.Workspace!.Tabs).Panels);
        Assert.Equal(RuntimePanelRecoveryKind.Browser, recoveredPanel.Kind);
        Assert.Equal(address.ToString(), recoveredPanel.StartupLocation);
        Assert.Null(recoveredPanel.ConnectionId);
        Assert.Null(recoveredPanel.FileLocation);

        panel.Dispose();
        panel.Dispose();
        Assert.Equal(1, lifetime.DisposeCount);
    }

    private static BrowserAddress Address(string value)
    {
        Assert.True(BrowserAddress.TryParse(value, out var address));
        return address;
    }

    private sealed class RecordingLifetime : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingBrowserRenderer : IBrowserRenderer
    {
        public BrowserSessionState State { get; } =
            BrowserSessionState.Initial(BrowserAddress.Blank);

        public CapabilitySet Capabilities { get; } = new(
        [
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserOriginGuard,
        ]);

        public event EventHandler<BrowserStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
            BrowserAddress address,
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>>
            NavigateWithinOriginAsync(
                BrowserOriginConstrainedNavigationRequest request,
                BrowserNavigationOrigin allowedOrigin,
                BrowserNavigationStartBinding startBinding,
                CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
            CaptureSnapshotAsync(
                BrowserDocumentBinding document,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Success(
                    new BrowserDocumentSnapshot(
                        document,
                        [new BrowserSnapshotNode(
                            0,
                            "document",
                            string.Empty)],
                        DateTimeOffset.UnixEpoch)));
        }

        public ValueTask<BrowserResult<BrowserClickReceipt>>
            ClickWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Success(
                    new BrowserClickReceipt(reference.Document)));
        }

        public ValueTask<BrowserResult<BrowserFillReceipt>>
            FillWithinOriginAsync(
                BrowserElementReference reference,
                string text,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Success(
                    new BrowserFillReceipt(reference.Document)));
        }

        public ValueTask<BrowserResult<BrowserCheckReceipt>>
            CheckWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Success(
                    new BrowserCheckReceipt(reference.Document)));
        }

        private ValueTask<BrowserResult<BrowserSessionState>> Success(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Success(State));
        }
    }

    public class NoopSessionClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
