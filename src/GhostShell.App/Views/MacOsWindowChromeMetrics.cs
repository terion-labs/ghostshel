using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GhostShell.App.Views;

/// <summary>
/// Reads the leading edge occupied by AppKit's standard window buttons.
/// Avalonia reports the native title-bar height but does not expose the
/// horizontal traffic-light bounds.
/// </summary>
internal static class MacOsWindowChromeMetrics
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const nint ZoomButton = 2;

    public static double? TryGetTrafficLightRightEdge(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return null;
        }

        var button = SendObject(
            handle.NSWindow,
            Selector("standardWindowButton:"),
            ZoomButton);
        if (button == 0)
        {
            return null;
        }

        var frame = SendRect(button, Selector("frame"));
        var rightEdge = frame.X + frame.Width;
        return double.IsFinite(rightEdge) && rightEdge is > 0 and < 400
            ? rightEdge
            : null;
    }

    private static NativeRect SendRect(nint receiver, nint selector) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? SendRectX64(receiver, selector)
            : objc_msgSend_rect(receiver, selector);

    private static NativeRect SendRectX64(nint receiver, nint selector)
    {
        objc_msgSend_stret(out var result, receiver, selector);
        return result;
    }

    private static nint Selector(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(
        double X,
        double Y,
        double Width,
        double Height);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(
        nint receiver,
        nint selector,
        nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern NativeRect objc_msgSend_rect(
        nint receiver,
        nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend_stret")]
    private static extern void objc_msgSend_stret(
        out NativeRect result,
        nint receiver,
        nint selector);
}
