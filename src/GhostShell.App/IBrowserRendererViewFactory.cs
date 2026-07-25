using Avalonia.Controls;
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

    public void Dispose() => _lifetime?.Dispose();
}
