using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class BrowserRuntimePanelViewModel : RuntimePanelViewModel
{
    private readonly BrowserRendererView _rendererView;
    private BrowserAddress _currentAddress;
    private bool _disposed;

    public BrowserRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        SessionOwner owner,
        BrowserAddress initialAddress,
        ISessionHostClient sessionClient,
        ClientId clientId,
        BrowserRendererView rendererView)
        : base(id, PanelKind.Browser, title, "Browser")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(initialAddress);
        SessionClient = sessionClient
            ?? throw new ArgumentNullException(nameof(sessionClient));
        ClientId = clientId;
        _rendererView = rendererView
            ?? throw new ArgumentNullException(nameof(rendererView));
        _currentAddress = initialAddress;
        SessionRequest = new EnsureBrowserSessionRequest(
            SessionId.New(),
            owner,
            title,
            initialAddress);
    }

    public ISessionHostClient SessionClient { get; }

    public ClientId ClientId { get; }

    public EnsureBrowserSessionRequest SessionRequest { get; }

    public BrowserRendererView RendererView => _rendererView;

    public BrowserAddress CurrentAddress
    {
        get => _currentAddress;
        private set => SetProperty(ref _currentAddress, value);
    }

    internal void ApplyBrowserState(BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        CurrentAddress = state.Address;
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rendererView.Dispose();
        base.Dispose();
    }
}
