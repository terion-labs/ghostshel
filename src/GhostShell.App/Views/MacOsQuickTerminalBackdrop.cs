using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GhostShell.App.Views;

/// <summary>
/// Applies a numeric backdrop blur radius on macOS. Avalonia exposes backdrop
/// blur as a capability tier, not a radius; the native compositor API used by
/// terminal applications retains the stored radius when available.
///
/// Used by the Quick Terminal and by the shell window, which puts the same
/// blur behind its base surface.
/// </summary>
internal static class MacOsQuickTerminalBackdrop
{
    private const string CoreGraphicsFramework =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    public static bool TryApply(Window window, int radius)
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
            var windowNumber = SendInteger(
                handle.NSWindow,
                sel_registerName("windowNumber"));
            if (windowNumber <= 0)
            {
                return false;
            }

            return CGSSetWindowBackgroundBlurRadius(
                CGSDefaultConnectionForThread(),
                checked((nuint)windowNumber),
                Math.Clamp(radius, 0, 100)) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport(CoreGraphicsFramework)]
    private static extern nint CGSDefaultConnectionForThread();

    [DllImport(CoreGraphicsFramework)]
    private static extern int CGSSetWindowBackgroundBlurRadius(
        nint connection,
        nuint windowNumber,
        int radius);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendInteger(nint receiver, nint selector);
}
