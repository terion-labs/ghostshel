using GhostShell.Application;

namespace GhostShell.App.Controls;

public sealed class NativeRendererKeyInputEventArgs(NativeRendererKeyInput input) : EventArgs
{
    public NativeRendererKeyInput Input { get; } = input;

    public bool Handled { get; set; }
}
