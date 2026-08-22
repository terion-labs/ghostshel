using Avalonia.Platform;
using Avalonia.Rendering.Composition;

namespace Exclr8Cef.WebView;

internal enum AcceleratedFrameCopyResult
{
    Unavailable,
    Copied,
    Failed,
}

/// <summary>
/// Owns one reusable client-side IOSurface and its Avalonia imports. The slot
/// only returns to CEF after the Metal timeline event confirms that Avalonia
/// finished consuming the previous frame.
/// </summary>
internal sealed class AcceleratedFrameSlot
{
    private enum SlotState
    {
        Available,
        Acquired,
        AwaitingConsumer,
        Disposed,
    }

    private readonly object _gate = new();
    private readonly MacAcceleratedFrame _frame = new();
    private SlotState _state;
    private bool _disposeRequested;
    private TaskCompletionSource? _disposeCompletion;
    private ICompositionImportedGpuImage? _image;
    private ICompositionImportedGpuSemaphore? _readyEvent;
    private IntPtr _importedSurfaceHandle;
    private IntPtr _importedEventHandle;

    public MacAcceleratedFrame Frame => _frame;

    public ICompositionImportedGpuImage Image =>
        _image ?? throw new InvalidOperationException(
            "The accelerated frame slot has no imported image.");

    public ICompositionImportedGpuSemaphore ReadyEvent =>
        _readyEvent ?? throw new InvalidOperationException(
            "The accelerated frame slot has no imported shared event.");

    public AcceleratedFrameCopyResult TryAcquireAndCopy(
        AcceleratedPaintEventArgs paint)
    {
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return AcceleratedFrameCopyResult.Unavailable;
            }

            if (_state == SlotState.AwaitingConsumer)
            {
                if (!_frame.IsReleasedByConsumer)
                {
                    return AcceleratedFrameCopyResult.Unavailable;
                }

                _state = SlotState.Available;
            }

            if (_state != SlotState.Available)
            {
                return AcceleratedFrameCopyResult.Unavailable;
            }

            _state = SlotState.Acquired;
            try
            {
                if (_frame.TryCopyFrom(paint))
                {
                    return AcceleratedFrameCopyResult.Copied;
                }
            }
            catch
            {
                _state = SlotState.Available;
                throw;
            }

            _state = SlotState.Available;
            return AcceleratedFrameCopyResult.Failed;
        }
    }

    public Task<bool> EnsureImportsAsync(ICompositionGpuInterop interop)
    {
        ArgumentNullException.ThrowIfNull(interop);

        IntPtr surfaceHandle;
        IntPtr eventHandle;
        int width;
        int height;
        Cef.CefColorType colorType;
        ICompositionImportedGpuImage? staleImage;
        ICompositionImportedGpuSemaphore? staleEvent;
        lock (_gate)
        {
            if (_state != SlotState.Acquired || _disposeRequested)
            {
                return Task.FromResult(false);
            }

            surfaceHandle = _frame.IOSurface;
            eventHandle = _frame.ReadyEvent;
            width = _frame.Width;
            height = _frame.Height;
            colorType = _frame.Format;
            bool importsMatch = _image is { IsLost: false }
                && _readyEvent is { IsLost: false }
                && _importedSurfaceHandle == surfaceHandle
                && _importedEventHandle == eventHandle;
            if (importsMatch)
            {
                return Task.FromResult(true);
            }

            staleImage = _image;
            staleEvent = _readyEvent;
            _image = null;
            _readyEvent = null;
            _importedSurfaceHandle = IntPtr.Zero;
            _importedEventHandle = IntPtr.Zero;
        }

        return ReplaceImportsAsync(
            interop,
            surfaceHandle,
            eventHandle,
            width,
            height,
            colorType,
            staleImage,
            staleEvent);
    }

    public void MarkSubmittedToConsumer()
    {
        bool beginDisposal;
        lock (_gate)
        {
            if (_state == SlotState.Disposed)
            {
                return;
            }
            if (_state != SlotState.Acquired)
            {
                throw new InvalidOperationException(
                    "Only an acquired accelerated frame can be submitted.");
            }

            _state = SlotState.AwaitingConsumer;
            beginDisposal = _disposeRequested;
            if (beginDisposal)
            {
                _state = SlotState.Disposed;
            }
        }

        if (beginDisposal)
        {
            StartDisposal(waitForConsumer: true);
        }
    }

    public void ReleaseWithoutPresentation()
    {
        bool beginDisposal;
        lock (_gate)
        {
            if (_state == SlotState.Disposed)
            {
                return;
            }
            if (_state != SlotState.Acquired)
            {
                throw new InvalidOperationException(
                    "Only an acquired accelerated frame can be released.");
            }

            _state = SlotState.Available;
            beginDisposal = _disposeRequested;
            if (beginDisposal)
            {
                _state = SlotState.Disposed;
            }
        }

        if (beginDisposal)
        {
            StartDisposal(waitForConsumer: false);
        }
    }

    public Task DisposeAsync()
    {
        bool beginDisposal;
        bool waitForConsumer;
        Task completion;
        lock (_gate)
        {
            if (_disposeCompletion is not null)
            {
                return _disposeCompletion.Task;
            }

            _disposeCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _disposeCompletion.Task;
            _disposeRequested = true;
            beginDisposal = _state != SlotState.Acquired;
            waitForConsumer = _state == SlotState.AwaitingConsumer;
            if (beginDisposal)
            {
                _state = SlotState.Disposed;
            }
        }

        if (beginDisposal)
        {
            StartDisposal(waitForConsumer);
        }

        return completion;
    }

    private async Task<bool> ReplaceImportsAsync(
        ICompositionGpuInterop interop,
        IntPtr surfaceHandle,
        IntPtr eventHandle,
        int width,
        int height,
        Cef.CefColorType colorType,
        ICompositionImportedGpuImage? staleImage,
        ICompositionImportedGpuSemaphore? staleEvent)
    {
        await DisposeImportsAsync(staleImage, staleEvent);

        lock (_gate)
        {
            if (_state != SlotState.Acquired || _disposeRequested)
            {
                return false;
            }
        }

        var format = colorType switch
        {
            Cef.CefColorType.Rgba8888 =>
                PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
            Cef.CefColorType.Bgra8888 =>
                PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
            _ => throw new NotSupportedException(
                "CEF returned an unsupported accelerated-paint format."),
        };
        var image = interop.ImportImage(
            new PlatformHandle(
                surfaceHandle,
                KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef),
            new PlatformGraphicsExternalImageProperties
            {
                Width = width,
                Height = height,
                Format = format,
                TopLeftOrigin = true,
            });
        ICompositionImportedGpuSemaphore readyEvent;
        try
        {
            readyEvent = interop.ImportSemaphore(
                new PlatformHandle(
                    eventHandle,
                    KnownPlatformGraphicsExternalSemaphoreHandleTypes
                        .MetalSharedEvent));
        }
        catch
        {
            await image.DisposeAsync();
            throw;
        }

        lock (_gate)
        {
            if (_state == SlotState.Acquired && !_disposeRequested)
            {
                _image = image;
                _readyEvent = readyEvent;
                _importedSurfaceHandle = surfaceHandle;
                _importedEventHandle = eventHandle;
                return true;
            }
        }

        await DisposeImportsAsync(image, readyEvent);
        return false;
    }

    private void StartDisposal(bool waitForConsumer) =>
        _ = CompleteDisposalAsync(waitForConsumer);

    private async Task CompleteDisposalAsync(bool waitForConsumer)
    {
        ICompositionImportedGpuImage? image;
        ICompositionImportedGpuSemaphore? readyEvent;
        TaskCompletionSource completion;
        lock (_gate)
        {
            image = _image;
            readyEvent = _readyEvent;
            _image = null;
            _readyEvent = null;
            _importedSurfaceHandle = IntPtr.Zero;
            _importedEventHandle = IntPtr.Zero;
            completion = _disposeCompletion
                ?? throw new InvalidOperationException(
                    "Accelerated frame disposal was not requested.");
        }

        try
        {
            while (waitForConsumer && !_frame.IsReleasedByConsumer)
            {
                await Task.Delay(1);
            }

            _frame.Dispose();
            await DisposeImportsAsync(image, readyEvent);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task DisposeImportsAsync(
        ICompositionImportedGpuImage? image,
        ICompositionImportedGpuSemaphore? readyEvent)
    {
        try
        {
            if (readyEvent is not null)
            {
                await readyEvent.DisposeAsync();
            }
        }
        finally
        {
            if (image is not null)
            {
                await image.DisposeAsync();
            }
        }
    }
}
