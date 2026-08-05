using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GhostShell.App.Views;

/// <summary>
/// Stops AppKit painting its own material in the title-bar band.
///
/// A window with standard decorations gets a title bar drawn by the system,
/// over the top of the client area — and it lightens whatever is beneath it.
/// Against an opaque shell that is invisible; against a translucent one it is a
/// distinctly paler strip across the top, with a seam where it ends. No fill of
/// ours can cancel it, because ours is underneath: the band was matched by
/// hand three times and the seam came back each time in one direction or the
/// other.
///
/// Asking for a transparent title bar is what the applications that do this
/// properly ask for — it keeps the standard window buttons, and stops only the
/// painting. Avalonia extends the client area under the decorations but does
/// not surface this, and its own <c>WindowDecorations.None</c> would take the
/// buttons with it.
/// </summary>
internal static class MacOsWindowTitleBar
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    /// <summary>NSWindowTitleVisibility.NSWindowTitleHidden.</summary>
    private const nint TitleHidden = 1;

    public static bool TryStopPaintingItsOwnMaterial(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return false;
        }

        try
        {
            SendBool(
                handle.NSWindow,
                sel_registerName("setTitlebarAppearsTransparent:"),
                true);
            // The title itself would otherwise still be drawn into a band that
            // no longer has anything behind it.
            SendNInt(
                handle.NSWindow,
                sel_registerName("setTitleVisibility:"),
                TitleHidden);
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendNInt(nint receiver, nint selector, nint value);
}
