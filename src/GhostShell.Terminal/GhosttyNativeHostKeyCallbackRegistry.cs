using System.Collections.Concurrent;
using System.Diagnostics;

namespace GhostShell.Terminal;

internal static class GhosttyNativeHostKeyCallbackRegistry
{
    private static readonly ConcurrentDictionary<
        nint,
        WeakReference<GhosttyNativeHostKeyCallbackRegistration>> Registrations = new();

    private static long _nextRegistrationId;

    internal static NativeTerminalHostKeyInterceptorV1 NativeCallback { get; } = Dispatch;

    internal static GhosttyNativeHostKeyCallbackRegistration Register(
        NativeTerminalHostKeyInterceptorV1 callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var id = NextRegistrationId();
        var registration = new GhosttyNativeHostKeyCallbackRegistration(id, callback);
        if (!Registrations.TryAdd(id, new WeakReference<GhosttyNativeHostKeyCallbackRegistration>(registration)))
        {
            throw new InvalidOperationException("The native host-key callback ID is already registered.");
        }

        return registration;
    }

    internal static void Unregister(nint id) => Registrations.TryRemove(id, out _);

    private static nint NextRegistrationId()
    {
        long id;
        do
        {
            id = Interlocked.Increment(ref _nextRegistrationId);
        }
        while (id == 0);

        return checked((nint)id);
    }

    private static bool Dispatch(
        nint userdata,
        in NativeTerminalHostKeyEventV1 keyEvent)
    {
        if (!Registrations.TryGetValue(userdata, out var weakRegistration)
            || !weakRegistration.TryGetTarget(out var registration))
        {
            Registrations.TryRemove(userdata, out _);
            return false;
        }

        try
        {
            var handled = registration.TryInvoke(userdata, in keyEvent);
            GC.KeepAlive(registration);
            return handled;
        }
        catch (Exception exception)
        {
            TraceFailureNoThrow(
                "The native terminal host-key callback failed: {0}",
                exception.GetType().Name);
            return false;
        }
    }

    internal static void TraceFailureNoThrow(string format, string exceptionType)
    {
        try
        {
            Trace.TraceError(format, exceptionType);
        }
        catch
        {
            // A custom trace listener is untrusted at this reverse P/Invoke boundary.
        }
    }
}

internal sealed class GhosttyNativeHostKeyCallbackRegistration : IDisposable
{
    private NativeTerminalHostKeyInterceptorV1? _callback;
    private int _disposed;

    internal GhosttyNativeHostKeyCallbackRegistration(
        nint id,
        NativeTerminalHostKeyInterceptorV1 callback)
    {
        Id = id;
        _callback = callback;
    }

    ~GhosttyNativeHostKeyCallbackRegistration()
    {
        DisposeCore();
    }

    internal nint Id { get; }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    internal bool TryInvoke(
        nint userdata,
        in NativeTerminalHostKeyEventV1 keyEvent)
    {
        var callback = Volatile.Read(ref _callback);
        return callback is not null && callback(userdata, in keyEvent);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GhosttyNativeHostKeyCallbackRegistry.Unregister(Id);
        Volatile.Write(ref _callback, null);
    }
}
