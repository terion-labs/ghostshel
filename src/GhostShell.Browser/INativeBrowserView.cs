using Avalonia.Controls;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Keeps the vendor control behind one small, testable boundary. Browser state
/// published outside this project uses only GhostSHELL application contracts.
/// </summary>
internal interface INativeBrowserView
{
    Control View { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    event EventHandler<NativeBrowserNavigationEventArgs>? NavigationStarted;

    event EventHandler<NativeBrowserNavigationCompletedEventArgs>? NavigationCompleted;

    event EventHandler<NativeBrowserNavigationRejectedEventArgs>?
        NavigationRejected;

    void Navigate(BrowserAddress address);

    bool GoBack();

    bool GoForward();

    bool Reload();

    bool Stop();

    Task<NativeBrowserSnapshotResult> CaptureSnapshotAsync();

    Task<NativeBrowserClickResult> ClickAsync(
        NativeBrowserElementHandle handle);

    Task<NativeBrowserFillResult> FillAsync(
        NativeBrowserElementHandle handle,
        string text);

    Task<NativeBrowserCheckResult> CheckAsync(
        NativeBrowserElementHandle handle);
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
    long navigationGeneration) : EventArgs
{
    public BrowserAddress? Address { get; } = address;

    public bool IsSuccess { get; } = isSuccess;

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
