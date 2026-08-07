using Avalonia.Controls;
using GhostShell.App.Controls;
using GhostShell.Application;

namespace GhostShell.App;

/// <summary>
/// Presentation-owned bridge between the Avalonia panel and a concrete native
/// browser renderer selected by the desktop composition root.
/// </summary>
public interface IBrowserRendererViewFactory
{
    BrowserRendererView Create();
}

/// <summary>
/// A panel's native browser: the operating system's view, the renderer that
/// drives it, and the session attachment that feeds it.
///
/// The attachment lives here rather than in whichever control happens to be
/// drawing the panel, because it belongs to the panel and the panel outlives its
/// views. Rearranging panels rebuilds those views; if the attachment went with
/// them, every layout change would tear a live session off its renderer and put
/// a new one back — which is a session lifetime decided by where a panel sits on
/// screen, and there are runs with no screen at all.
/// </summary>
public sealed class BrowserRendererView(
    Control view,
    IBrowserRenderer renderer,
    IDisposable? lifetime = null) : IDisposable
{
    private readonly IDisposable? _lifetime = lifetime;

    public Control View { get; } =
        view ?? throw new ArgumentNullException(nameof(view));

    public IBrowserRenderer Renderer { get; } =
        renderer ?? throw new ArgumentNullException(nameof(renderer));

    /// <summary>
    /// The session this renderer is attached to, once it is. A view that comes
    /// along later adopts this instead of attaching again.
    /// </summary>
    internal BrowserRendererAttachment? Attachment { get; set; }

    /// <summary>
    /// The layer holding <see cref="View"/>, so releasing it is possible from
    /// here — the panel is the only thing that knows the surface is finished
    /// with, and by then no view is left to ask.
    /// </summary>
    internal NativeSurfaceLayer? Layer { get; set; }

    public void Dispose()
    {
        var attachment = Attachment;
        Attachment = null;
        attachment?.Release();
        Layer?.Release(View);
        Layer = null;
        _lifetime?.Dispose();
    }
}
