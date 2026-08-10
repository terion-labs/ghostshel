using Avalonia.Controls;
using GhostShell.App.Controls;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

/// <summary>
/// Presentation-owned bridge between the Avalonia panel and the browser
/// renderer selected by the desktop composition root.
/// </summary>
public interface IBrowserRendererViewFactory
{
    /// <summary>Creates a direct local browser for lightweight embedded previews.</summary>
    BrowserRendererView Create();

    ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken);
}

/// <summary>
/// A panel's browser visual, the renderer that drives it, and the session
/// attachment that feeds it.
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
    /// The presentation host currently drawing <see cref="View"/>. The panel
    /// owns the visual, so a replacement host adopts it without changing the
    /// renderer or session lifetime.
    /// </summary>
    internal BrowserPresentationHost? PresentationHost { get; set; }

    public void Dispose()
    {
        var attachment = Attachment;
        Attachment = null;
        attachment?.Release();
        var presentationHost = PresentationHost;
        PresentationHost = null;
        presentationHost?.ReleaseRendererVisual(this);
        _lifetime?.Dispose();
    }
}
