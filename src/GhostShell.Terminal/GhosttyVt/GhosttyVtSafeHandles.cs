using Microsoft.Win32.SafeHandles;

namespace GhostShell.Terminal.GhosttyVt;

internal sealed class GhosttyVtTerminalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtTerminalHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtTerminalHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.TerminalFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtRenderStateHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtRenderStateHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtRenderStateHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.RenderStateFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtRenderRowIteratorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtRenderRowIteratorHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtRenderRowIteratorHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.RenderRowIteratorFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtRenderRowCellsHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtRenderRowCellsHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtRenderRowCellsHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.RenderRowCellsFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKeyEventHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtKeyEventHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKeyEventHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KeyEventFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKeyEncoderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtKeyEncoderHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKeyEncoderHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KeyEncoderFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtMouseEventHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtMouseEventHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtMouseEventHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.MouseEventFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtMouseEncoderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtMouseEncoderHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtMouseEncoderHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.MouseEncoderFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKittyPlacementIteratorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtKittyPlacementIteratorHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKittyPlacementIteratorHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KittyPlacementIteratorFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKittyVirtualPlacementIteratorHandle
    : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtKittyVirtualPlacementIteratorHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKittyVirtualPlacementIteratorHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KittyVirtualPlacementIteratorFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtTrackedGridRefHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GhosttyVtTrackedGridRefHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtTrackedGridRefHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.TrackedGridRefFree(handle);
        return true;
    }
}
