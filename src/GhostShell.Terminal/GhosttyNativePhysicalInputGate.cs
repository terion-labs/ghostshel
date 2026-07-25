using System.Runtime.InteropServices;
using GhostShell.Application;

namespace GhostShell.Terminal;

internal sealed class GhosttyNativePhysicalInputGate : IDisposable
{
    private readonly GhosttyTerminalHandle _terminal;
    private readonly GhosttyNativePhysicalInputCallbackRegistration _callbackRegistration;
    private Func<NativeRendererPhysicalInput, bool>? _gate;
    private int _disposed;

    private GhosttyNativePhysicalInputGate(
        GhosttyTerminalHandle terminal,
        Func<NativeRendererPhysicalInput, bool> gate)
    {
        _terminal = terminal;
        _gate = gate;
        _callbackRegistration = GhosttyNativePhysicalInputCallbackRegistry.Register(Accept);
        try
        {
            if (!GhosttyNativeMethods.TerminalSetPhysicalInputGateV1(
                    terminal,
                    GhosttyNativePhysicalInputCallbackRegistry.NativeCallback,
                    _callbackRegistration.Id))
            {
                throw new GhosttyNativeException(
                    "Unable to install the native terminal physical-input gate.");
            }
        }
        catch
        {
            Volatile.Write(ref _gate, null);
            _callbackRegistration.Dispose();
            throw;
        }
    }

    public static GhosttyNativePhysicalInputGate Attach(
        GhosttyTerminalHandle terminal,
        Func<NativeRendererPhysicalInput, bool>? gate)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return new(
            terminal,
            gate ?? throw new InvalidOperationException(
                "The native terminal requires a host-owned physical-input gate."));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_terminal.IsClosed && !_terminal.IsInvalid)
            {
                _ = GhosttyNativeMethods.TerminalSetPhysicalInputGateV1(
                    _terminal,
                    null,
                    0);
            }
        }
        finally
        {
            Volatile.Write(ref _gate, null);
            _callbackRegistration.Dispose();
        }
    }

    private bool Accept(
        nint userdata,
        in NativeTerminalPhysicalInputEventV1 inputEvent)
    {
        _ = userdata;
        var gate = Volatile.Read(ref _gate);
        if (gate is null || !TryMap(inputEvent, out var input))
        {
            return false;
        }

        try
        {
            return gate(input);
        }
        catch (Exception exception)
        {
            GhosttyNativePhysicalInputCallbackRegistry.TraceFailureNoThrow(
                "The native terminal physical-input gate failed: {0}",
                exception.GetType().Name);
            return false;
        }
    }

    internal static bool TryMap(
        NativeTerminalPhysicalInputEventV1 native,
        out NativeRendererPhysicalInput input)
    {
        input = default;
        if (native.Version != 1
            || native.StructSize < (uint)Marshal.SizeOf<NativeTerminalPhysicalInputEventV1>()
            || native.Reserved != 0
            || native.AuthorityEpoch == 0
            || native.Kind > (uint)NativeRendererPhysicalInputKind.MouseScroll)
        {
            return false;
        }

        input = new NativeRendererPhysicalInput(
            (NativeRendererPhysicalInputKind)native.Kind);
        return true;
    }
}
