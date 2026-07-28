using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GhostShell.Terminal;

/// <summary>
/// Reports the native terminal view taking first responder back to the host.
///
/// Clicking a native child view never reaches Avalonia's focus system, so without
/// this the shell cannot tell which panel the keyboard is in — the panel stays
/// marked inactive until something else moves focus for it.
/// </summary>
internal sealed class GhosttyNativeFocusObserver : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FocusCallback(nint userdata);

    /// <summary>
    /// Held for the process lifetime so the native side never calls through a
    /// collected delegate.
    /// </summary>
    private static readonly FocusCallback Callback = OnFocus;

    private static readonly nint CallbackPointer =
        Marshal.GetFunctionPointerForDelegate(Callback);

    private static readonly ConcurrentDictionary<int, Action> Observers = new();

    private static int _nextId;

    private readonly GhosttyTerminalHandle _terminal;
    private readonly int _id;
    private int _disposed;

    private GhosttyNativeFocusObserver(GhosttyTerminalHandle terminal, Action observer)
    {
        _terminal = terminal;
        _id = Interlocked.Increment(ref _nextId);
        Observers[_id] = observer;
        try
        {
            if (!GhosttyNativeMethods.TerminalSetFocusObserverV1(
                    terminal,
                    CallbackPointer,
                    _id))
            {
                throw new GhosttyNativeException(
                    "Unable to install the native terminal focus observer.");
            }
        }
        catch
        {
            Observers.TryRemove(_id, out _);
            throw;
        }
    }

    public static GhosttyNativeFocusObserver Attach(
        GhosttyTerminalHandle terminal,
        Action observer)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(observer);
        return new(terminal, observer);
    }

    private static void OnFocus(nint userdata)
    {
        if (Observers.TryGetValue((int)userdata, out var observer))
        {
            observer();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Unbind before dropping the callback, so a focus change already in
        // flight cannot arrive after the observer is gone.
        if (!_terminal.IsInvalid)
        {
            _ = GhosttyNativeMethods.TerminalSetFocusObserverV1(_terminal, 0, 0);
        }

        Observers.TryRemove(_id, out _);
    }
}
