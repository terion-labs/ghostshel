using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// Owns a macOS GPU copy of one CEF accelerated-paint frame. The copied
/// IOSurface is independent from CEF's recyclable surface pool and can be
/// imported by a compositor until this object is disposed.
/// </summary>
public sealed unsafe class MacAcceleratedFrame : IDisposable
{
    private excef_macos_accelerated_frame _native;
    private int _disposed;

    private MacAcceleratedFrame(excef_macos_accelerated_frame native)
    {
        _native = native;
    }

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
    /// Copies the borrowed CEF IOSurface into an owned IOSurface using Metal.
    /// This method must run before the accelerated-paint callback returns.
    /// </summary>
    public static MacAcceleratedFrame? TryCopy(AcceleratedPaintEventArgs paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        if (!OperatingSystem.IsMacOS() || paint.SharedHandle == IntPtr.Zero)
        {
            return null;
        }

        excef_macos_accelerated_frame native = default;
        int copied = Excef.excef_copy_macos_accelerated_frame(
            paint.SharedHandle.ToPointer(),
            paint.CodedWidth,
            paint.CodedHeight,
            (int)paint.Format,
            &native);
        return copied == 1 ? new MacAcceleratedFrame(native) : null;
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
