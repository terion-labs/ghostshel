namespace GhostShell.Application;

/// <summary>
/// Attaches a platform rendering and input surface to a terminal session.
/// </summary>
public interface ITerminalRendererAttachment
{
    ValueTask AttachRendererAsync(
        NativeRendererHost rendererHost,
        CancellationToken cancellationToken);

    ValueTask DetachRendererAsync(CancellationToken cancellationToken);

    ValueTask FocusAsync(CancellationToken cancellationToken);
}
