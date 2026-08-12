using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GhostShell.App.Views;

/// <summary>
/// Slides Avalonia's macOS backdrop and Skia surface in one AppKit context.
///
/// Avalonia's <c>AutoFitContentView</c> directly owns the behind-window
/// <c>NSVisualEffectView</c> and the <c>AvnView</c> that presents Skia. A visual
/// effect does not obey ordinary layer transforms or masks: AppKit continues
/// producing its effect for the view's real bounds. The reveal therefore
/// changes those native bounds. The material grows down from zero height while
/// the full-size Skia view moves down by the same distance.
/// </summary>
internal static class MacOsQuickTerminalReveal
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AvaloniaContentView = "AutoFitContentView";
    private const string AvaloniaRenderView = "AvnView";
    private const nint BlendsBehindWindow = 0;
    private const nint AlwaysActive = 1;
    public static bool TryClearWindowBacking(Window window)
    {
        if (!TryGetNativeWindow(window, out var nsWindow))
        {
            return false;
        }

        var clearColor = SendId(objc_getClass("NSColor"), Selector("clearColor"));
        if (clearColor == 0)
        {
            return false;
        }

        SendIdArgument(nsWindow, Selector("setBackgroundColor:"), clearColor);
        return true;
    }

    /// <summary>
    /// Prevents AppKit from swapping the material to its inactive, flat fill
    /// before an outside-click dismissal has finished sliding off screen.
    /// </summary>
    public static bool TryKeepBackdropActive(Window window)
    {
        if (!TryGetRevealViews(window, out var views) || views.BlurView == 0)
        {
            return false;
        }

        SendIdArgument(views.BlurView, Selector("setState:"), AlwaysActive);
        return true;
    }

    public static bool TrySetProgress(Window window, double progress)
    {
        if (!TryGetRevealViews(window, out var views))
        {
            return false;
        }

        SetRevealFrames(views, progress);
        return true;
    }

    public static bool TryAnimate(
        Window window,
        double from,
        double to,
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TrySetProgress(window, to);
        }

        if (!TryGetRevealViews(window, out var views))
        {
            return false;
        }

        SetRevealFrames(views, from);
        var contextClass = objc_getClass("NSAnimationContext");
        SendVoid(contextClass, Selector("beginGrouping"));
        var context = SendId(contextClass, Selector("currentContext"));
        SendDouble(context, Selector("setDuration:"), duration.TotalSeconds);
        var timing = CubicEaseOutTiming();
        if (timing != 0)
        {
            SendIdArgument(context, Selector("setTimingFunction:"), timing);
        }

        var target = FramesForProgress(views.Width, views.Height, to);
        if (views.BlurView != 0)
        {
            var blurAnimator = SendId(views.BlurView, Selector("animator"));
            SendRectArgument(blurAnimator, Selector("setFrame:"), target.Blur);
        }

        var contentAnimator = SendId(views.ContentView, Selector("animator"));
        SendRectArgument(contentAnimator, Selector("setFrame:"), target.Content);
        SendVoid(contextClass, Selector("endGrouping"));
        return true;
    }

    private static void SetRevealFrames(RevealViews views, double progress)
    {
        var frames = FramesForProgress(views.Width, views.Height, progress);
        if (views.BlurView != 0)
        {
            SendRectArgument(views.BlurView, Selector("setFrame:"), frames.Blur);
        }

        SendRectArgument(views.ContentView, Selector("setFrame:"), frames.Content);
    }

    private static RevealFrames FramesForProgress(
        double width,
        double height,
        double progress)
    {
        var visibleHeight = height * Math.Clamp(progress, 0, 1);
        var offset = height - visibleHeight;
        return new RevealFrames(
            new CGRect
            {
                X = 0,
                Y = offset,
                Width = width,
                Height = visibleHeight,
            },
            new CGRect
            {
                X = 0,
                Y = offset,
                Width = width,
                Height = height,
            });
    }

    private static bool TryGetRevealViews(Window window, out RevealViews views)
    {
        views = default;
        if (!TryGetNativeWindow(window, out var nsWindow))
        {
            return false;
        }

        var contentView = SendId(nsWindow, Selector("contentView"));
        var container = FindDescendantOfClass(contentView, AvaloniaContentView);
        if (container == 0)
        {
            return false;
        }

        var bounds = SendRect(container, Selector("bounds"));
        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);
        nint blurView = 0;
        nint renderView = 0;
        foreach (var subview in SubviewsOf(container))
        {
            var className = ClassNameOf(subview);
            if (string.Equals(className, AvaloniaRenderView, StringComparison.Ordinal))
            {
                renderView = subview;
                continue;
            }

            if (string.Equals(className, "NSVisualEffectView", StringComparison.Ordinal)
                && SendNInt(subview, Selector("blendingMode")) == BlendsBehindWindow
                && !SendBoolWithResult(subview, Selector("isHidden")))
            {
                blurView = subview;
            }
        }

        if (renderView == 0)
        {
            return false;
        }

        views = new RevealViews(renderView, blurView, width, height);
        return true;
    }

    private static bool TryGetNativeWindow(Window window, out nint nsWindow)
    {
        nsWindow = 0;
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return false;
        }

        nsWindow = handle.NSWindow;
        return true;
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

    private static nint CubicEaseOutTiming() => SendFourFloats(
        objc_getClass("CAMediaTimingFunction"),
        Selector("functionWithControlPoints::::"),
        1f / 3f,
        1,
        2f / 3f,
        1);

    private static string ClassNameOf(nint instance) => instance == 0
        ? string.Empty
        : Marshal.PtrToStringAnsi(object_getClassName(instance)) ?? string.Empty;

    private static nint Selector(string name) => sel_registerName(name);

    private readonly record struct RevealViews(
        nint ContentView,
        nint BlurView,
        double Width,
        double Height);

    private readonly record struct RevealFrames(CGRect Blur, CGRect Content);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint instance);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SendBoolWithResult(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendNInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendIdArgument(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(nint receiver, nint selector, double value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendFourFloats(
        nint receiver,
        nint selector,
        float firstX,
        float firstY,
        float secondX,
        float secondY);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdAtIndex(
        nint receiver,
        nint selector,
        nuint index);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendRectArgument(
        nint receiver,
        nint selector,
        CGRect value);
}
