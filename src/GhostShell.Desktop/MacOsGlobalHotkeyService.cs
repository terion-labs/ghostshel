using System.Runtime.InteropServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class MacOsGlobalHotkeyService : IGlobalHotkeyService
{
    private const string CarbonLibrary =
        "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const int NoError = 0;
    private const int EventNotHandledError = -9874;
    private const int EventHotKeyExistsError = -9878;
    private const int EventHotKeyInvalidError = -9879;
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 5;
    private const uint EventParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;
    private const uint GhostShellSignature = 0x4753484C;
    private const uint GhostShellHotKeyId = 1;
    private const uint GhostShellEscapeHotKeyId = 2;
    private const uint EventHotKeyExclusive = 1;
    private const uint CommandKey = 1U << 8;
    private const uint ShiftKey = 1U << 9;
    private const uint OptionKey = 1U << 11;
    private const uint ControlKey = 1U << 12;
    private const uint GraveVirtualKeyCode = 50;
    private const uint EscapeVirtualKeyCode = 53;

    private readonly EventHandlerDelegate _eventHandler;
    private IntPtr _eventHandlerReference;
    private IntPtr _hotKeyReference;
    private IntPtr _escapeHotKeyReference;
    private bool _disposed;

    public MacOsGlobalHotkeyService()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The Carbon hot-key adapter requires macOS.");
        }

        _eventHandler = HandleEvent;
    }

    public event EventHandler? Pressed;

    public event EventHandler? EscapePressed;

    public KeyStroke? RegisteredGesture { get; private set; }

    public GlobalHotkeyRegistrationResult Register(KeyStroke gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister();
        if (!TryMapGesture(gesture, out var keyCode, out var modifiers))
        {
            return Failure(
                GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                "global_hotkey_invalid_gesture",
                "The selected shortcut is not supported by the macOS hot-key adapter.");
        }

        var installStatus = EnsureHandler();
        if (installStatus != NoError)
        {
            return NativeFailure(installStatus);
        }

        var hotKeyId = new EventHotKeyId(GhostShellSignature, GhostShellHotKeyId);
        var registrationStatus = RegisterEventHotKey(
            keyCode,
            modifiers,
            hotKeyId,
            GetApplicationEventTarget(),
            EventHotKeyExclusive,
            out _hotKeyReference);
        if (registrationStatus != NoError)
        {
            ReleaseUnexpectedRegistration(ref _hotKeyReference);
            RemoveHandlerIfUnused();
            return registrationStatus switch
            {
                EventHotKeyExistsError => Failure(
                    GlobalHotkeyRegistrationErrorCode.Conflict,
                    "global_hotkey_conflict",
                    "Another application already owns this global shortcut."),
                EventHotKeyInvalidError => Failure(
                    GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                    "global_hotkey_invalid_gesture",
                    "macOS rejected the selected global shortcut."),
                _ => NativeFailure(registrationStatus),
            };
        }

        RegisteredGesture = gesture;
        return new GlobalHotkeyRegistrationResult.Success(gesture);
    }

    public void Unregister()
    {
        if (_hotKeyReference != IntPtr.Zero)
        {
            _ = UnregisterEventHotKey(_hotKeyReference);
            _hotKeyReference = IntPtr.Zero;
        }

        RemoveHandlerIfUnused();
        RegisteredGesture = null;
    }

    public GlobalHotkeyRegistrationResult BeginEscapeCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EndEscapeCapture();
        var installStatus = EnsureHandler();
        if (installStatus != NoError)
        {
            return NativeFailure(installStatus);
        }

        var hotKeyId = new EventHotKeyId(GhostShellSignature, GhostShellEscapeHotKeyId);
        var registrationStatus = RegisterEventHotKey(
            EscapeVirtualKeyCode,
            0,
            hotKeyId,
            GetApplicationEventTarget(),
            EventHotKeyExclusive,
            out _escapeHotKeyReference);
        if (registrationStatus != NoError)
        {
            ReleaseUnexpectedRegistration(ref _escapeHotKeyReference);
            RemoveHandlerIfUnused();
            return registrationStatus switch
            {
                EventHotKeyExistsError => Failure(
                    GlobalHotkeyRegistrationErrorCode.Conflict,
                    "escape_capture_conflict",
                    "Another application already owns Escape while Quick Terminal is open."),
                EventHotKeyInvalidError => Failure(
                    GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                    "escape_capture_invalid",
                    "macOS rejected transient Escape capture."),
                _ => NativeFailure(registrationStatus),
            };
        }

        return new GlobalHotkeyRegistrationResult.Success(new KeyStroke("ESCAPE"));
    }

    public void EndEscapeCapture()
    {
        if (_escapeHotKeyReference != IntPtr.Zero)
        {
            _ = UnregisterEventHotKey(_escapeHotKeyReference);
            _escapeHotKeyReference = IntPtr.Zero;
        }

        RemoveHandlerIfUnused();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EndEscapeCapture();
        Unregister();
        _disposed = true;
    }

    private int HandleEvent(IntPtr nextHandler, IntPtr eventReference, IntPtr userData)
    {
        _ = nextHandler;
        _ = userData;
        var status = GetEventParameter(
            eventReference,
            EventParamDirectObject,
            TypeEventHotKeyId,
            IntPtr.Zero,
            (uint)Marshal.SizeOf<EventHotKeyId>(),
            IntPtr.Zero,
            out var hotKeyId);
        if (status != NoError || hotKeyId.Signature != GhostShellSignature)
        {
            return EventNotHandledError;
        }

        switch (hotKeyId.Id)
        {
            case GhostShellHotKeyId:
                Pressed?.Invoke(this, EventArgs.Empty);
                return NoError;
            case GhostShellEscapeHotKeyId:
                EscapePressed?.Invoke(this, EventArgs.Empty);
                return NoError;
            default:
                return EventNotHandledError;
        }
    }

    private int EnsureHandler()
    {
        if (_eventHandlerReference != IntPtr.Zero)
        {
            return NoError;
        }

        var eventType = new EventTypeSpec(EventClassKeyboard, EventHotKeyPressed);
        var status = InstallEventHandler(
            GetApplicationEventTarget(),
            _eventHandler,
            1,
            ref eventType,
            IntPtr.Zero,
            out _eventHandlerReference);
        if (status != NoError)
        {
            _eventHandlerReference = IntPtr.Zero;
        }

        return status;
    }

    private static void ReleaseUnexpectedRegistration(ref IntPtr hotKeyReference)
    {
        if (hotKeyReference == IntPtr.Zero)
        {
            return;
        }

        _ = UnregisterEventHotKey(hotKeyReference);
        hotKeyReference = IntPtr.Zero;
    }

    private void RemoveHandlerIfUnused()
    {
        if (_eventHandlerReference == IntPtr.Zero
            || _hotKeyReference != IntPtr.Zero
            || _escapeHotKeyReference != IntPtr.Zero)
        {
            return;
        }

        _ = RemoveEventHandler(_eventHandlerReference);
        _eventHandlerReference = IntPtr.Zero;
    }

    private static bool TryMapGesture(KeyStroke gesture, out uint keyCode, out uint modifiers)
    {
        keyCode = 0;
        modifiers = 0;
        if (gesture.Key is not ("`" or "GRAVE" or "OEMTILDE"))
        {
            return false;
        }

        keyCode = GraveVirtualKeyCode;
        if ((gesture.Modifiers & KeyModifiers.Meta) != 0)
        {
            modifiers |= CommandKey;
        }

        if ((gesture.Modifiers & KeyModifiers.Shift) != 0)
        {
            modifiers |= ShiftKey;
        }

        if ((gesture.Modifiers & KeyModifiers.Alt) != 0)
        {
            modifiers |= OptionKey;
        }

        if ((gesture.Modifiers & KeyModifiers.Control) != 0)
        {
            modifiers |= ControlKey;
        }

        return modifiers != 0;
    }

    private static GlobalHotkeyRegistrationResult Failure(
        GlobalHotkeyRegistrationErrorCode code,
        string stableCode,
        string message) =>
        new GlobalHotkeyRegistrationResult.Failure(new(code, stableCode, message));

    private static GlobalHotkeyRegistrationResult NativeFailure(int status) => Failure(
        GlobalHotkeyRegistrationErrorCode.NativeFailure,
        "global_hotkey_native_failure",
        $"macOS could not register the global shortcut (OSStatus {status}).");

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventTypeSpec(uint EventClass, uint EventKind);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventHotKeyId(uint Signature, uint Id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerDelegate(
        IntPtr nextHandler,
        IntPtr eventReference,
        IntPtr userData);

    [DllImport(CarbonLibrary)]
    private static extern int InstallEventHandler(
        IntPtr eventTarget,
        EventHandlerDelegate handler,
        uint eventTypeCount,
        ref EventTypeSpec eventTypes,
        IntPtr userData,
        out IntPtr eventHandlerReference);

    [DllImport(CarbonLibrary)]
    private static extern int RemoveEventHandler(IntPtr eventHandlerReference);

    [DllImport(CarbonLibrary)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(CarbonLibrary)]
    private static extern int RegisterEventHotKey(
        uint hotKeyCode,
        uint hotKeyModifiers,
        EventHotKeyId hotKeyId,
        IntPtr target,
        uint options,
        out IntPtr hotKeyReference);

    [DllImport(CarbonLibrary)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyReference);

    [DllImport(CarbonLibrary)]
    private static extern int GetEventParameter(
        IntPtr eventReference,
        uint parameterName,
        uint desiredType,
        IntPtr actualType,
        uint bufferSize,
        IntPtr actualSize,
        out EventHotKeyId data);
}
