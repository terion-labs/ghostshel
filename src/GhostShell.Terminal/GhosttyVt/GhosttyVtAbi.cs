using System.Runtime.InteropServices;

namespace GhostShell.Terminal.GhosttyVt;

internal static class GhosttyVtAbi
{
    internal static bool TryValidateManagedLayouts(out string detail)
    {
        if (IntPtr.Size != 8)
        {
            detail = "GhostSHELL currently packages libghostty-vt for 64-bit processes only.";
            return false;
        }

        (Type Type, int ExpectedSize)[] layouts =
        [
            (typeof(GhosttyVtString), 16),
            (typeof(GhosttyVtColorRgb), 3),
            (typeof(GhosttyVtStyleColorValue), 8),
            (typeof(GhosttyVtStyleColor), 16),
            (typeof(GhosttyVtStyle), 72),
            (typeof(GhosttyVtSemanticPromptEvent), 24),
            (typeof(GhosttyVtTerminalScrollbar), 24),
            (typeof(GhosttyVtScrollViewport), 24),
            (typeof(GhosttyVtRenderRowSelection), 16),
            (typeof(GhosttyVtRenderStateColors), 792),
            (typeof(GhosttyVtPointCoordinate), 8),
            (typeof(GhosttyVtPoint), 24),
            (typeof(GhosttyVtGridRef), 24),
            (typeof(GhosttyVtSelection), 64),
            (typeof(GhosttyVtTerminalSearchOptions), 40),
            (typeof(GhosttyVtTerminalSearchResult), 32),
            (typeof(GhosttyVtSelectWordOptions), 48),
            (typeof(GhosttyVtSelectWordBetweenOptions), 72),
            (typeof(GhosttyVtSelectLineOptions), 56),
            (typeof(GhosttyVtSelectionFormatOptions), 24),
            (typeof(GhosttyVtKey), 4),
            (typeof(GhosttyVtMode), 2),
            (typeof(GhosttyVtMousePosition), 8),
            (typeof(GhosttyVtMouseEncoderSize), 40),
            (typeof(GhosttyVtKittyPlacementRenderInfo), 56),
            (typeof(GhosttyVtKittyVirtualPlacementRenderInfo), 64),
        ];

        foreach (var (type, expectedSize) in layouts)
        {
            var actualSize = Marshal.SizeOf(type);
            if (actualSize == expectedSize)
            {
                continue;
            }

            detail = $"Managed ABI layout mismatch for {type.Name}: expected {expectedSize}, found {actualSize}.";
            return false;
        }

        detail = "Managed libghostty-vt ABI layouts are valid.";
        return true;
    }
}
