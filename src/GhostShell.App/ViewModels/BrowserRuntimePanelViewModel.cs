using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class BrowserRuntimePanelViewModel : RuntimePanelViewModel
{
    private readonly object _initializationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IBrowserRendererViewFactory _rendererViewFactory;
    private readonly ConnectionProfile _connection;
    private BrowserRendererView? _rendererView;
    private BrowserAddress _currentAddress;
    private Task _initialization = Task.CompletedTask;
    private string? _routeErrorMessage;
    private bool _initializationStarted;
    private bool _disposed;

    public BrowserRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        SessionOwner owner,
        BrowserAddress initialAddress,
        ISessionHostClient sessionClient,
        ClientId clientId,
        ConnectionProfile connection,
        IBrowserRendererViewFactory rendererViewFactory)
        : base(id, PanelKind.Browser, title, "Browser")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(initialAddress);
        SessionClient = sessionClient
            ?? throw new ArgumentNullException(nameof(sessionClient));
        ClientId = clientId;
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            throw new ArgumentException(
                "A browser connection must be local or SSH.",
                nameof(connection));
        }

        _rendererViewFactory = rendererViewFactory
            ?? throw new ArgumentNullException(nameof(rendererViewFactory));
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

    public ConnectionId ConnectionId => _connection.Id;

    public string ConnectionDisplayName => _connection.Endpoint is ConnectionEndpoint.Local
        ? "Local"
        : _connection.Name;

    public BrowserRendererView? RendererView
    {
        get => _rendererView;
        private set => SetProperty(ref _rendererView, value);
    }

    public string? RouteErrorMessage
    {
        get => _routeErrorMessage;
        private set
        {
            if (SetProperty(ref _routeErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasRouteError));
            }
        }
    }

    public bool HasRouteError => RouteErrorMessage is not null;

    internal bool HasInteractiveAttachment =>
        RendererView?.Attachment?.Matches(
            SessionClient,
            ClientId,
            SessionRequest.SessionId) is true;

    internal async Task EnsureHostedRendererAsync(
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await StartInitialization().WaitAsync(linkedCancellation.Token);
        var renderer = RendererView
            ?? throw new InvalidOperationException(
                "The browser renderer is unavailable.");
        _ = await renderer.EnsureAttachmentAsync(
            SessionClient,
            ClientId,
            SessionRequest,
            ViewportDescriptor.Empty,
            linkedCancellation.Token);
    }

    public Task StartInitialization()
    {
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializationStarted)
            {
                return _initialization;
            }

            _initializationStarted = true;
            _initialization = InitializeAsync();
            return _initialization;
        }
    }

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

    private async Task InitializeAsync()
    {
        try
        {
            var renderer = await _rendererViewFactory
                .CreateAsync(_connection, _lifetime.Token);
            if (_disposed)
            {
                renderer.Dispose();
                return;
            }

            RendererView = renderer;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RouteErrorMessage = exception.Message;
            throw;
        }
    }

    public override void Dispose()
    {
        lock (_initializationGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        RendererView?.Dispose();
        RendererView = null;
        base.Dispose();
    }
}
