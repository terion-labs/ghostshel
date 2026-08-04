using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GhostShell.Browser;

/// <summary>
/// Sets a native view's appearance, which is what WebKit answers
/// <c>prefers-color-scheme</c> from. Verified both ways: a page loaded
/// under an appearance reports it, and a page already loaded receives the
/// media-query change event when the appearance moves under it.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacColorScheme
{
    private const string Runtime = "/usr/lib/libobjc.dylib";

    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kCFStringEncodingUTF8.</summary>
    private const uint Utf8 = 0x08000100;

    private static readonly IntPtr AppearanceClass = GetClass("NSAppearance");
    private static readonly IntPtr AppearanceNamed = Selector("appearanceNamed:");
    private static readonly IntPtr SetAppearance = Selector("setAppearance:");
    private static readonly IntPtr LightName = CreateString("NSAppearanceNameAqua");
    private static readonly IntPtr DarkName = CreateString("NSAppearanceNameDarkAqua");

    public static void Apply(IntPtr view, bool light)
    {
        if (view == IntPtr.Zero || AppearanceClass == IntPtr.Zero)
        {
            return;
        }

        var appearance = SendPointer(
            AppearanceClass,
            AppearanceNamed,
            light ? LightName : DarkName);
        if (appearance != IntPtr.Zero)
        {
            SendVoid(view, SetAppearance, appearance);
        }
    }

    [DllImport(Runtime, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(Runtime, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    private static extern IntPtr CFStringCreate(IntPtr allocator, string value, uint encoding);

    /// <summary>
    /// Held for the process: two constant names, created once, never freed
    /// because they are never replaced.
    /// </summary>
    private static IntPtr CreateString(string value) =>
        CFStringCreate(IntPtr.Zero, value, Utf8);
}
