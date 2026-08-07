using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GhostShell.App.Controls;

/// <summary>
/// Where the shell's operating-system views live.
///
/// A webview is not an Avalonia visual: it is a native control the framework
/// hosts, and the framework destroys that control the moment its host leaves the
/// visual tree and builds a fresh one on the way back. So a panel that held its
/// own webview lost the page whenever anything moved — adding a panel beside it,
/// splitting it, switching tabs — because rearranging panels rebuilds the views
/// that draw them. The session survived every time; the document did not.
///
/// The mistake was letting a panel's <em>view</em> own something whose life is
/// the panel's. The visual tree is the wrong thing to hang any of this on in the
/// first place: a workspace in the background and a headless run have no visual
/// tree at all, and both have to behave exactly like a visible one.
///
/// So native surfaces live here instead, parented once for as long as the panel
/// that owns them exists. A panel says where to show one and when to hide it;
/// neither answer ever removes it. Layout becomes geometry and nothing else.
/// </summary>
internal sealed class NativeSurfaceLayer : Canvas
{
    public NativeSurfaceLayer()
    {
        // The layer covers the window but is not a surface of its own: it is
        // hit-testable exactly where a native view sits and nowhere else, so the
        // interface underneath keeps working around them.
        Background = null;
        ClipToBounds = true;
    }

    /// <summary>
    /// The layer for a control's window, or null before it has one.
    ///
    /// The shell's own window declares one. A panel floated into a window of its
    /// own gets one made here, because a floated panel is still a panel and its
    /// surface still has to outlive the views that draw it.
    /// </summary>
    public static NativeSurfaceLayer? For(Visual visual)
    {
        if (visual.FindAncestorOfType<Window>() is not { } window)
        {
            return null;
        }

        if (window.GetVisualDescendants().OfType<NativeSurfaceLayer>().FirstOrDefault()
            is { } declared)
        {
            return declared;
        }

        if (window.Content is not Control content)
        {
            return null;
        }

        var layer = new NativeSurfaceLayer();
        window.Content = new Panel { Children = { content, layer } };
        return layer;
    }

    /// <summary>
    /// Shows a surface at the given place in this layer, adopting it if this is
    /// the first time it has been seen.
    /// </summary>
    public void Present(Control surface, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!Children.Contains(surface))
        {
            Children.Add(surface);
        }

        // A degenerate rect is what an unmeasured viewport reports, and moving a
        // native view to zero size is how some hosts decide it is gone.
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            surface.IsVisible = false;
            return;
        }

        SetLeft(surface, bounds.X);
        SetTop(surface, bounds.Y);
        surface.Width = bounds.Width;
        surface.Height = bounds.Height;
        surface.IsVisible = true;
    }

    /// <summary>
    /// Stops showing a surface without giving it up. The panel it belongs to is
    /// off screen — another tab is in front, or its view is being rebuilt — and
    /// will ask for it again.
    /// </summary>
    public void Conceal(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (Children.Contains(surface))
        {
            surface.IsVisible = false;
        }
    }

    /// <summary>
    /// Gives a surface up, for good. Only the owner of the panel calls this, when
    /// the panel itself is gone.
    /// </summary>
    public void Release(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Children.Remove(surface);
    }
}
