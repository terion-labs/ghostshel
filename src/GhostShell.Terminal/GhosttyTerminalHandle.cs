using Microsoft.Win32.SafeHandles;

namespace GhostShell.Terminal;

internal sealed class GhosttyTerminalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyTerminalHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyTerminalHandle(nint nativeHandle)
        : this()
    {
        SetHandle(nativeHandle);
    }

    protected override bool ReleaseHandle()
    {
        GhosttyNativeMethods.TerminalDetach(handle);
        return true;
    }
}
