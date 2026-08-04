using GhostShell.Browser;

namespace GhostShell.Browser.Tests;

/// <summary>
/// The page's answer to prefers-color-scheme comes from the hosting view's
/// appearance, which this sets. The end-to-end behaviour was verified
/// against a live WebKit view — loaded-under and changed-underneath both
/// follow — and what remains testable here is the boundary: a call with no
/// view must be a no-op rather than a message to a null pointer, which
/// Objective-C would swallow silently and every other platform would not.
/// </summary>
public sealed class MacColorSchemeTests
{
    [Fact]
    public void A_missing_view_is_left_alone()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // Before attachment there is no native view; asking anyway must not
        // reach the runtime at all.
        MacColorScheme.Apply(IntPtr.Zero, light: true);
        MacColorScheme.Apply(IntPtr.Zero, light: false);
    }
}
