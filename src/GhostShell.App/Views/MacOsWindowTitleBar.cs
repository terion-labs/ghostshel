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

    /// <summary>NSWindowStyleMaskFullSizeContentView.</summary>
    private const nuint FullSizeContentView = 1 << 15;

    public static MacOsTitleBarOutcome TryStopPaintingItsOwnMaterial(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return MacOsTitleBarOutcome.Unreachable;
        }

        try
        {
            // The content has to be allowed under the title bar before asking
            // the title bar not to paint — without this the window still
            // reserves the band and fills it, and the ask above it does
            // nothing while reporting that it worked.
            var styleMask = SendNUInt(handle.NSWindow, sel_registerName("styleMask"));
            var contentAlreadyRanFullSize = (styleMask & FullSizeContentView) != 0;
            if (!contentAlreadyRanFullSize)
            {
                SendNUIntArgument(
                    handle.NSWindow,
                    sel_registerName("setStyleMask:"),
                    styleMask | FullSizeContentView);
            }

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
            // Reading it back rather than trusting the set: the previous
            // version of this reported success on the strength of the call
            // having returned, which said nothing about whether anything
            // changed. This distinguishes the two cases that matter — the bit
            // was already on and the band survived anyway, or we turned it on.
            var settled = (SendNUInt(handle.NSWindow, sel_registerName("styleMask"))
                & FullSizeContentView) != 0;
            return settled
                ? contentAlreadyRanFullSize
                    ? MacOsTitleBarOutcome.ContentAlreadyRanFullSize
                    : MacOsTitleBarOutcome.ContentNowRunsFullSize
                : MacOsTitleBarOutcome.Refused;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return MacOsTitleBarOutcome.Unreachable;
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

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendNUIntArgument(
        nint receiver,
        nint selector,
        nuint value);
}

/// <summary>
/// What asking the title bar to stop painting actually did.
/// </summary>
internal enum MacOsTitleBarOutcome
{
    /// <summary>Not macOS, or no window to ask.</summary>
    Unreachable,

    /// <summary>Asked, and the style mask still does not carry the bit.</summary>
    Refused,

    /// <summary>Avalonia had already extended the content; we only asked for transparency.</summary>
    ContentAlreadyRanFullSize,

    /// <summary>The content did not run under the title bar until we said so.</summary>
    ContentNowRunsFullSize,
}
