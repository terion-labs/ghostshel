using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GhostShell.App.Views;

/// <summary>
/// Lets the shell's base surface run to the top edge of the window.
///
/// A translucent shell with an extended client area still showed a paler strip
/// across the top, exactly as tall as the title bar, and a hairline under it.
/// Turning the base surface opaque made both vanish, which places whatever
/// draws them <em>behind</em> the base rather than over it — visible only
/// through the fraction of the base that is not opaque. That rules out every
/// fill of ours, and it is why matching the band by hand never worked in
/// either direction.
///
/// They are Avalonia's. Its macOS content view keeps a title-bar material and
/// a separator box, and <c>SetExtendClientArea</c> reveals both whenever the
/// window has full decorations — the two are wired together in the backend
/// with nothing managed in between, so extending under the decorations and
/// having the decorations painted are the same switch. The shell wants the
/// first without the second, so the two views are hidden directly.
///
/// Only those two. The standard window buttons live in the title-bar container
/// and are not touched, so the window keeps its frame, rounded corners, resize
/// edges and traffic lights.
/// </summary>
internal static class MacOsWindowTitleBar
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    /// <summary>Avalonia's macOS content view, the parent of both views.</summary>
    private const string AvaloniaContentView = "AutoFitContentView";

    /// <summary>NSVisualEffectBlendingModeWithinWindow.</summary>
    private const nint BlendsWithinWindow = 1;

    /// <summary>
    /// Hides the title-bar material and its underline, and reports how many it
    /// found. Safe to call repeatedly: Avalonia re-shows them when the
    /// decorations or the full-screen state change.
    /// </summary>
    public static MacOsTitleBarOutcome TryLetTheBaseSurfaceRunToTheTop(Window window)
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
            var contentView = SendId(handle.NSWindow, Selector("contentView"));
            if (contentView == 0)
            {
                return MacOsTitleBarOutcome.NoContentView;
            }

            var root = contentView;
            for (var next = SendId(root, Selector("superview"));
                next != 0;
                next = SendId(root, Selector("superview")))
            {
                root = next;
            }

            var avaloniaContent = FindDescendantOfClass(root, AvaloniaContentView);
            if (avaloniaContent == 0)
            {
                return MacOsTitleBarOutcome.NoContentView;
            }

            var found = 0;
            foreach (var subview in SubviewsOf(avaloniaContent))
            {
                if (!PaintsTheTitleBarBand(subview))
                {
                    continue;
                }

                found++;
                if (!SendBoolResult(subview, Selector("isHidden")))
                {
                    SendBool(subview, Selector("setHidden:"), true);
                }
            }

            return found == 0
                ? MacOsTitleBarOutcome.NothingToHide
                : MacOsTitleBarOutcome.Hidden;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return MacOsTitleBarOutcome.Unreachable;
        }
    }

    /// <summary>
    /// The material and the separator, and neither of the things beside them.
    /// The blending mode is what separates the title-bar material from the
    /// window-wide blur, which shares its class, sits behind the content and is
    /// the shell's own backdrop when it is asked for.
    /// </summary>
    private static bool PaintsTheTitleBarBand(nint view) => ClassNameOf(view) switch
    {
        "NSVisualEffectView" =>
            SendNInt(view, Selector("blendingMode")) == BlendsWithinWindow,
        "NSBox" => true,
        _ => false,
    };

    private static nint FindDescendantOfClass(nint view, string className)
    {
        if (string.Equals(ClassNameOf(view), className, StringComparison.Ordinal))
        {
            return view;
        }

        foreach (var subview in SubviewsOf(view))
        {
            var found = FindDescendantOfClass(subview, className);
            if (found != 0)
            {
                return found;
            }
        }

        return 0;
    }

    private static IEnumerable<nint> SubviewsOf(nint view)
    {
        var subviews = SendId(view, Selector("subviews"));
        if (subviews == 0)
        {
            yield break;
        }

        var count = SendNUInt(subviews, Selector("count"));
        for (nuint index = 0; index < count; index++)
        {
            var subview = SendIdAtIndex(subviews, Selector("objectAtIndex:"), index);
            if (subview != 0)
            {
                yield return subview;
            }
        }
    }

    private static string ClassNameOf(nint instance) => instance == 0
        ? string.Empty
        : Marshal.PtrToStringAnsi(object_getClassName(instance)) ?? string.Empty;

    /// <summary>
    /// Does what double-clicking a title bar does on this desktop.
    ///
    /// The shell draws its own tab bar across that band and hands every press
    /// to the window's move-drag, so the second click of a double-click never
    /// reaches the platform as one. What the gesture means is a system
    /// setting — zoom, minimise, or nothing — so it is read rather than
    /// assumed.
    /// </summary>
    public static bool TryDoWhatADoubleClickDoes(Window window)
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
            var action = StandardUserDefaultsString("AppleActionOnDoubleClick");
            switch (action)
            {
                case "Minimize":
                    SendIdArgument(handle.NSWindow, Selector("performMiniaturize:"), 0);
                    return true;
                case "None":
                    return true;
                default:
                    // Unset means Maximize, which is this desktop's default.
                    SendIdArgument(handle.NSWindow, Selector("performZoom:"), 0);
                    return true;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    private static string? StandardUserDefaultsString(string key)
    {
        var defaults = SendId(
            objc_getClass("NSUserDefaults"),
            Selector("standardUserDefaults"));
        if (defaults == 0)
        {
            return null;
        }

        var name = SendIdArgumentReturningId(
            objc_getClass("NSString"),
            Selector("stringWithUTF8String:"),
            Marshal.StringToHGlobalAnsi(key));
        if (name == 0)
        {
            return null;
        }

        var value = SendIdArgumentReturningId(defaults, Selector("stringForKey:"), name);
        return value == 0
            ? null
            : Marshal.PtrToStringUTF8(SendId(value, Selector("UTF8String")));
    }

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendIdArgument(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdArgumentReturningId(
        nint receiver,
        nint selector,
        nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint instance);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolResult(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendNInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdAtIndex(nint receiver, nint selector, nuint index);
}

/// <summary>
/// What asking Avalonia's content view to stop painting the band actually did.
/// </summary>
internal enum MacOsTitleBarOutcome
{
    /// <summary>Not macOS, or no window to ask.</summary>
    Unreachable,

    /// <summary>Avalonia's content view was not where it was looked for.</summary>
    NoContentView,

    /// <summary>The content view was found, and nothing in it paints the band.</summary>
    NothingToHide,

    /// <summary>The material and its underline are hidden.</summary>
    Hidden,
}
