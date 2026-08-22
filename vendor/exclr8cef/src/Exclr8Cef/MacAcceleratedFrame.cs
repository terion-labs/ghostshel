using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// Owns a reusable macOS GPU destination for CEF accelerated-paint frames.
/// The IOSurface is independent from CEF's recyclable surface pool. Callers
/// must not copy another frame into it until the compositor releases the
/// previous frame.
/// </summary>
public sealed unsafe class MacAcceleratedFrame : IDisposable
{
    private excef_macos_accelerated_frame _native;
    private int _disposed;

    ~MacAcceleratedFrame() => Dispose();

    public IntPtr IOSurface => GetHandle(_native.io_surface);

    public IntPtr ReadyEvent => GetHandle(_native.ready_event);

    public ulong ReadyValue
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _native.ready_value;
        }
    }

    public int Width
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _native.width;
        }
    }

    public int Height
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _native.height;
        }
    }

    public Cef.CefColorType Format
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return (Cef.CefColorType)_native.format;
        }
    }

    /// <summary>
    /// True before the first copy and after the compositor has signaled that
    /// it finished consuming the most recently submitted frame.
    /// </summary>
    public bool IsReleasedByConsumer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            fixed (excef_macos_accelerated_frame* native = &_native)
            {
                return Excef.excef_macos_accelerated_frame_is_released(native)
                    == 1;
            }
        }
    }

    /// <summary>
    /// Copies the borrowed CEF IOSurface into an owned IOSurface using Metal.
    /// This method must run before the accelerated-paint callback returns.
    /// </summary>
    public static MacAcceleratedFrame? TryCopy(AcceleratedPaintEventArgs paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        var frame = new MacAcceleratedFrame();
        if (frame.TryCopyFrom(paint))
        {
            return frame;
        }

        frame.Dispose();
        return null;
    }

    /// <summary>
    /// Copies a borrowed CEF IOSurface into this object's client-owned
    /// destination. Matching dimensions and format reuse the existing
    /// IOSurface, Metal texture, and shared event.
    /// </summary>
    public bool TryCopyFrom(AcceleratedPaintEventArgs paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!OperatingSystem.IsMacOS() || paint.SharedHandle == IntPtr.Zero)
        {
            return false;
        }

        fixed (excef_macos_accelerated_frame* native = &_native)
        {
            return Excef.excef_copy_macos_accelerated_frame(
                paint.SharedHandle.ToPointer(),
                paint.CodedWidth,
                paint.CodedHeight,
                (int)paint.Format,
                native) == 1;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        fixed (excef_macos_accelerated_frame* native = &_native)
        {
            Excef.excef_release_macos_accelerated_frame(native);
        }
        GC.SuppressFinalize(this);
    }

    private IntPtr GetHandle(void* handle)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return (IntPtr)handle;
    }
}
