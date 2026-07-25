using GhostShell.Core;

namespace GhostShell.Application;

public sealed record NativeRendererHost(
    string HandleDescriptor,
    nint Handle,
    ViewportDescriptor Viewport,
    Func<NativeRendererKeyInput, bool>? KeyInterceptor = null,
    Func<NativeRendererPhysicalInput, bool>? PhysicalInputGate = null);

/// <summary>
/// One physical key press raised by a native renderer before it reaches the terminal engine.
/// The interceptor is synchronous so the renderer can either consume the press or pass it through
/// unchanged. Implementations must return promptly and schedule longer-running work separately.
/// </summary>
public readonly record struct NativeRendererKeyInput(KeyStroke Stroke, bool IsRepeat);

/// <summary>
/// One physical input about to reach a native terminal renderer. The session host installs
/// the gate and synchronously reclaims the exact human attachment before returning true.
/// Implementations must not block on asynchronous client or transport work.
/// </summary>
public readonly record struct NativeRendererPhysicalInput(NativeRendererPhysicalInputKind Kind);

public enum NativeRendererPhysicalInputKind
{
    KeyDown = 0,
    KeyUp = 1,
    ModifiersChanged = 2,
    ImePreedit = 3,
    ImeCommit = 4,
    Paste = 5,
    MouseMove = 6,
    MouseDrag = 7,
    MouseButtonDown = 8,
    MouseButtonUp = 9,
    MouseScroll = 10,
}
