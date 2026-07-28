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

            // Reported once so that silence afterwards is readable: with this line
            // present and no refusals, keystrokes are not reaching the gate at all,
            // which points above it rather than at the gate.
            if (Interlocked.Exchange(ref _installReported, 1) == 0)
            {
                Write("physical-input gate installed for the first terminal surface");
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
        if (gate is null)
        {
            TraceRefusal("no host gate is bound");
            return false;
        }

        if (!TryMap(inputEvent, out var input))
        {
            TraceRefusal(
                $"the event did not describe input this build understands — "
                + $"kind={inputEvent.Kind} epoch={inputEvent.AuthorityEpoch} "
                + $"version={inputEvent.Version} size={inputEvent.StructSize} "
                + $"(expected {Marshal.SizeOf<NativeTerminalPhysicalInputEventV1>()}) "
                + $"reserved={inputEvent.Reserved}");
            return false;
        }

        try
        {
            var accepted = gate(input);
            if (!accepted)
            {
                TraceRefusal($"the session refused it — {input.Kind}");
            }
            else
            {
                TraceAccepted(input.Kind);
            }

            return accepted;
        }
        catch (Exception exception)
        {
            GhosttyNativePhysicalInputCallbackRegistry.TraceFailureNoThrow(
                "The native terminal physical-input gate failed: {0}",
                exception.GetType().Name);
            return false;
        }
    }

    private static int _installReported;

    private static int _refusalsReported;

    /// <summary>
    /// A refused keystroke is silent by design — the surface simply ignores it —
    /// which makes a stuck gate look exactly like a dead terminal from the outside.
    /// Refusals are therefore always reported rather than hidden behind a switch:
    /// a terminal that will not accept typing is never normal, and needing to know
    /// the reason in advance to be told it is no use to anyone debugging one.
    ///
    /// Only the first few are printed. A held-down key would otherwise fill the
    /// output with the same line, and the reason does not change.
    /// </summary>
    private static void TraceRefusal(string reason)
    {
        var reported = Interlocked.Increment(ref _refusalsReported);
        if (reported > RefusalReportLimit)
        {
            return;
        }

        Write(
            reported == RefusalReportLimit
                ? $"input refused: {reason} (further refusals will not be reported)"
                : $"input refused: {reason}");
    }

    private const int RefusalReportLimit = 5;

    private static readonly int[] AcceptancesReported =
        new int[Enum.GetValues<NativeRendererPhysicalInputKind>().Length];

    /// <summary>
    /// Accepted input runs per event, so it cannot all be reported. The first few of
    /// each kind are, because silence otherwise means two different things —
    /// input never arriving, and input arriving and being let through — and those
    /// want opposite investigations. After that it takes
    /// <c>GHOSTSHELL_TRACE_INPUT=1</c>.
    ///
    /// The budget is per kind because moving the pointer across the terminal emits
    /// a continuous stream: a shared budget was spent on mouse movement before a
    /// key was ever pressed, and reported nothing about the keyboard, which was the
    /// only thing being asked about.
    /// </summary>
    private static void TraceAccepted(NativeRendererPhysicalInputKind kind)
    {
        var index = (int)kind;
        if (index >= 0
            && index < AcceptancesReported.Length
            && Interlocked.Increment(ref AcceptancesReported[index]) <= AcceptanceReportLimit)
        {
            Write($"input accepted: {kind}");
            return;
        }

        if (Environment.GetEnvironmentVariable("GHOSTSHELL_TRACE_INPUT") == "1")
        {
            Write($"input accepted: {kind}");
        }
    }

    private const int AcceptanceReportLimit = 3;

    private static void Write(string message)
    {
        try
        {
            Console.Error.WriteLine($"[ghostshell:input] {message}");
        }
        catch
        {
            // Diagnostics must never take down the reverse P/Invoke boundary.
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
