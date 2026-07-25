using System.Collections.Concurrent;
using System.Diagnostics;

namespace GhostShell.Terminal;

internal static class GhosttyNativePhysicalInputCallbackRegistry
{
    private static readonly ConcurrentDictionary<
        nint,
        WeakReference<GhosttyNativePhysicalInputCallbackRegistration>> Registrations = new();

    private static long _nextRegistrationId;

    internal static NativeTerminalPhysicalInputGateV1 NativeCallback { get; } = Dispatch;

    internal static GhosttyNativePhysicalInputCallbackRegistration Register(
        NativeTerminalPhysicalInputGateV1 callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var id = NextRegistrationId();
        var registration = new GhosttyNativePhysicalInputCallbackRegistration(id, callback);
        if (!Registrations.TryAdd(
                id,
                new WeakReference<GhosttyNativePhysicalInputCallbackRegistration>(registration)))
        {
            throw new InvalidOperationException(
                "The native physical-input callback ID is already registered.");
        }

        return registration;
    }

    internal static void Unregister(nint id) => Registrations.TryRemove(id, out _);

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
        in NativeTerminalPhysicalInputEventV1 inputEvent)
    {
        if (!Registrations.TryGetValue(userdata, out var weakRegistration)
            || !weakRegistration.TryGetTarget(out var registration))
        {
            Registrations.TryRemove(userdata, out _);
            return false;
        }

        try
        {
            var accepted = registration.TryInvoke(userdata, in inputEvent);
            GC.KeepAlive(registration);
            return accepted;
        }
        catch (Exception exception)
        {
            TraceFailureNoThrow(
                "The native terminal physical-input callback failed: {0}",
                exception.GetType().Name);
            return false;
        }
    }
}

internal sealed class GhosttyNativePhysicalInputCallbackRegistration : IDisposable
{
    private NativeTerminalPhysicalInputGateV1? _callback;
    private int _disposed;

    internal GhosttyNativePhysicalInputCallbackRegistration(
        nint id,
        NativeTerminalPhysicalInputGateV1 callback)
    {
        Id = id;
        _callback = callback;
    }

    ~GhosttyNativePhysicalInputCallbackRegistration()
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
        in NativeTerminalPhysicalInputEventV1 inputEvent)
    {
        var callback = Volatile.Read(ref _callback);
        return callback is not null && callback(userdata, in inputEvent);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GhosttyNativePhysicalInputCallbackRegistry.Unregister(Id);
        Volatile.Write(ref _callback, null);
    }
}
