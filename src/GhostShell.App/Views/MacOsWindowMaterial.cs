using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GhostShell.App.Views;

/// <summary>
/// Chooses which of the platform's materials the window sits on.
///
/// Avalonia creates the view but pins it to <c>NSVisualEffectMaterialLight</c>
/// — a fixed, long-deprecated light material, with nothing managed to change
/// it. A dark shell over a light material is why the base surface had to be
/// most of the way opaque to look right: the fill was doing the work the
/// material should be doing.
///
/// It also leaves the view's state at its default, which follows whether the
/// window is active, so the glass goes flat whenever focus moves elsewhere.
/// </summary>
internal static class MacOsWindowMaterial
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    /// <summary>Avalonia's macOS content view, the parent of the material.</summary>
    private const string AvaloniaContentView = "AutoFitContentView";

    /// <summary>The view Avalonia draws into, a sibling of the material.</summary>
    private const string AvaloniaDrawingView = "AvnView";

    /// <summary>NSVisualEffectBlendingModeBehindWindow.</summary>
    private const nint BlendsBehindWindow = 0;

    /// <summary>NSVisualEffectStateActive: the glass does not dull when unfocused.</summary>
    private const nint AlwaysActive = 1;

    /// <summary>
    /// Any top level, not only the window: a flyout is a window of its own on
    /// this platform, and the glass behind one is the same glass.
    /// </summary>
    public static bool TrySit(TopLevel window, MacOsMaterial material) =>
        TrySit(window, material, cornerRadius: null);

    /// <summary>
    /// A popup's window is a square, and the effect view fills it. The card
    /// inside is rounded, so without masking the blur to the same radius the
    /// square corners of the window stand outside it — which is the block that
    /// appeared at the corner of every menu once the glass went in.
    ///
    /// The window itself is left alone: rounding that would take the platform's
    /// shadow with it.
    /// </summary>
    public static bool TrySit(TopLevel window, MacOsMaterial material, double? cornerRadius)
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
            var contentView = SendId(handle.NSWindow, Selector("contentView"));
            if (contentView == 0)
            {
                return false;
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
                return false;
            }

            foreach (var subview in SubviewsOf(avaloniaContent))
            {
                // The window-wide one, not the title bar's: they share a class
                // and the blending mode is what tells them apart.
                if (!string.Equals(ClassNameOf(subview), "NSVisualEffectView", StringComparison.Ordinal)
                    || SendNInt(subview, Selector("blendingMode")) != BlendsBehindWindow)
                {
                    continue;
                }

                SendNIntArgument(subview, Selector("setMaterial:"), (nint)material);
                SendNIntArgument(subview, Selector("setState:"), AlwaysActive);
                if (cornerRadius is > 0 and { } radius
                    && SendId(subview, Selector("layer")) is var layer and not 0)
                {
                    SendDoubleArgument(layer, Selector("setCornerRadius:"), radius);
                    SendBoolArgument(layer, Selector("setMasksToBounds:"), true);
                }

                TryLetTheGlassThrough(avaloniaContent);
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Avalonia's drawing view answers YES to <c>isOpaque</c>, which tells the
    /// platform not to compose anything behind it. Where the shell then draws
    /// nothing — outside a rounded card, in the corners of the square window a
    /// popup gets — the pixels are whatever the backing store last held, and
    /// that is the block that appeared at each corner once there was glass
    /// behind it to be hidden.
    ///
    /// The flag itself belongs to the view and cannot be answered for it, but
    /// what governs the backing store is the layer, and that can be told.
    /// </summary>
    /// <summary>
    /// Hides the chrome a popup inherits but never wanted.
    ///
    /// Avalonia builds a popup's window from the same content view as a real
    /// one, so a popup gets the title bar's own material and its underline —
    /// square, full width, and unrounded. Behind a rounded card they are what
    /// fills the corners, which is the block that survived masking the glass,
    /// clearing the root, and letting the drawing view compose: none of those
    /// were drawing it.
    ///
    /// The window's own material is the one left alone. It is told apart the
    /// same way it is everywhere else, by what it blends with.
    /// </summary>
    public static bool TryHideInheritedChrome(TopLevel window)
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
            var contentView = SendId(handle.NSWindow, Selector("contentView"));
            var avaloniaContent = contentView == 0
                ? 0
                : FindDescendantOfClass(contentView, AvaloniaContentView);
            if (avaloniaContent == 0)
            {
                return false;
            }

            var hidAny = false;
            foreach (var subview in SubviewsOf(avaloniaContent))
            {
                var className = ClassNameOf(subview);
                var isForeignMaterial =
                    string.Equals(className, "NSVisualEffectView", StringComparison.Ordinal)
                    && SendNInt(subview, Selector("blendingMode")) != BlendsBehindWindow;
                if (!isForeignMaterial
                    && !string.Equals(className, "NSBox", StringComparison.Ordinal))
                {
                    continue;
                }

                SendBoolArgument(subview, Selector("setHidden:"), true);
                hidAny = true;
            }

            return hidAny;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    private static void TryLetTheGlassThrough(nint avaloniaContent)
    {
        var view = FindDescendantOfClass(avaloniaContent, AvaloniaDrawingView);
        if (view == 0)
        {
            return;
        }

        SendBoolArgument(view, Selector("setWantsLayer:"), true);
        if (SendId(view, Selector("layer")) is var layer and not 0)
        {
            SendBoolArgument(layer, Selector("setOpaque:"), false);
        }
    }

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

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint instance);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendNInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendNIntArgument(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdAtIndex(nint receiver, nint selector, nuint index);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendDoubleArgument(nint receiver, nint selector, double value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBoolArgument(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);
}

/// <summary>
/// The NSVisualEffectMaterial values worth a window's base surface.
/// </summary>
internal enum MacOsMaterial : long
{
    /// <summary>
    /// The base a window itself sits on. Flatter and less tinted than
    /// NSVisualEffectMaterialHUDWindow (13), which is built for floating
    /// panels and leans darker — both were tried against a bright backdrop.
    /// </summary>
    UnderWindowBackground = 21,
}
