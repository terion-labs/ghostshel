using Avalonia.Controls;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Keeps the vendor control behind one small, testable boundary. Browser state
/// published outside this project uses only GhostSHELL application contracts.
/// </summary>
internal interface IEmbeddedBrowserView : IDisposable
{
    Control View { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    void SetAgentActivity(bool isActive);

    event EventHandler<NativeBrowserNavigationEventArgs>? NavigationStarted;

    event EventHandler<NativeBrowserNavigationCompletedEventArgs>? NavigationCompleted;

    /// <summary>
    /// Raised when the main document changes its visible address without
    /// committing a different document, for example via history.pushState.
    /// </summary>
    event EventHandler<NativeBrowserAddressChangedEventArgs>? AddressChanged;

    event EventHandler<NativeBrowserNavigationRejectedEventArgs>?
        NavigationRejected;

    /// <summary>
    /// Raised after Chromium's renderer process terminates unexpectedly. The
    /// owner must replace this view; continuing to use a frozen OSR surface
    /// would make the visible browser disagree with its session state.
    /// </summary>
    event EventHandler? RenderProcessFailed;

    void Navigate(BrowserAddress address);

    bool GoBack();

    bool GoForward();

    bool Reload();

    bool Stop();

    Task<NativeBrowserSnapshotResult> CaptureSnapshotAsync(
        BrowserSnapshotQuery? query = null);

    Task<NativeBrowserClickResult> ClickAsync(
        NativeBrowserElementHandle handle);

    Task<NativeBrowserFillResult> FillAsync(
        NativeBrowserElementHandle handle,
        string text);

    Task<NativeBrowserCheckResult> CheckAsync(
        NativeBrowserElementHandle handle);

    Task<NativeBrowserElementStateResult> ReadElementStateAsync(
        NativeBrowserElementHandle handle);

    void BeginNetworkActivityObservation();

    void EndNetworkActivityObservation();

    NativeBrowserNetworkActivity ReadNetworkActivity();

    Task<NativeBrowserViewport> ReadViewportAsync();

    Task<NativeBrowserAutomationResult> DispatchMouseAsync(BrowserMouseRequest request);

    Task<NativeBrowserAutomationResult> DispatchKeyAsync(BrowserKeyRequest request);

    Task<NativeBrowserAutomationResult> DispatchScrollAsync(BrowserScrollRequest request);

    Task<NativeBrowserAutomationResult> EvaluateAsync(BrowserEvaluateRequest request);
}

internal sealed class NativeBrowserAddressChangedEventArgs(
    BrowserAddress address) : EventArgs
{
    public BrowserAddress Address { get; } =
        address ?? throw new ArgumentNullException(nameof(address));
}

internal sealed class NativeBrowserNavigationEventArgs(
    BrowserAddress address,
    long navigationGeneration) : EventArgs
{
    public BrowserAddress Address { get; } =
        address ?? throw new ArgumentNullException(nameof(address));

    public long NavigationGeneration { get; } =
        navigationGeneration > 0
            ? navigationGeneration
            : throw new ArgumentOutOfRangeException(
                nameof(navigationGeneration));

    public bool Cancel { get; set; }
}

internal sealed class NativeBrowserNavigationCompletedEventArgs(
    BrowserAddress? address,
    bool isSuccess,
    long navigationGeneration,
    bool wasStopped = false) : EventArgs
{
    public BrowserAddress? Address { get; } = address;

    public bool IsSuccess { get; } = wasStopped && isSuccess
        ? throw new ArgumentException(
            "A stopped navigation cannot also be successful.",
            nameof(isSuccess))
        : isSuccess;

    /// <summary>
    /// The host explicitly requested Stop and CEF acknowledged it with this
    /// terminal event. This is neither a committed document nor a load error.
    /// </summary>
    public bool WasStopped { get; } = wasStopped;

    public long NavigationGeneration { get; } =
        navigationGeneration > 0
            ? navigationGeneration
            : throw new ArgumentOutOfRangeException(
                nameof(navigationGeneration));
}

internal sealed class NativeBrowserNavigationRejectedEventArgs(
    NativeBrowserNavigationRejectionReason reason,
    long navigationGeneration) : EventArgs
{
    public NativeBrowserNavigationRejectionReason Reason { get; } =
        Enum.IsDefined(reason)
            ? reason
            : throw new ArgumentOutOfRangeException(nameof(reason));

    public long NavigationGeneration { get; } =
        navigationGeneration > 0
            ? navigationGeneration
            : throw new ArgumentOutOfRangeException(
                nameof(navigationGeneration));
}

internal enum NativeBrowserNavigationRejectionReason
{
    UnsupportedAddress,
    OriginPolicy,
}
