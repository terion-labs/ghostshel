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
    /// <summary>
    /// Every layer alive, so a suspension reaches all of them. Panels can be
    /// floated into windows of their own, and a drag that started in one window
    /// has to be visible in every window it might land in.
    /// </summary>
    private static readonly List<NativeSurfaceLayer> Layers = [];

    private static int _suspensions;

    private readonly Dictionary<Control, bool> _wanted = [];

    public NativeSurfaceLayer()
    {
        // The layer covers the window but is not a surface of its own: it is
        // hit-testable exactly where a native view sits and nowhere else, so the
        // interface underneath keeps working around them.
        Background = null;
        ClipToBounds = true;
        // Registered from birth, not from being shown. A layer that joined only
        // once it was attached would miss a suspension raised before it got
        // there, and come up with its surfaces on top of whatever asked for the
        // screen.
        Layers.Add(this);
    }

    /// <summary>
    /// Takes every native surface off the screen until the returned handle is
    /// disposed.
    ///
    /// There is no z-order to appeal to here. Avalonia draws its whole scene into
    /// one surface and the operating system composites native views above it, so
    /// nothing the shell draws can be on top of a webview — which is why the
    /// dock's placement targets never appeared while dragging a panel over one.
    /// Being above everything is not negotiable; being there at all is. So for
    /// the moment the shell needs its own pixels seen, the surfaces step aside.
    ///
    /// This costs nothing now that a surface outlives being hidden: no reload, no
    /// re-attach, no navigation. The page is exactly where it was.
    /// </summary>
    public static IDisposable Suspend()
    {
        if (Interlocked.Increment(ref _suspensions) == 1)
        {
            ApplyAll();
        }

        return new Suspension();
    }

    /// <summary>
    /// Raised when surfaces step aside and when they come back, so a panel can
    /// show what it is in the gap.
    /// </summary>
    public static event EventHandler? SuspensionChanged;

    public static bool IsSuspended => Volatile.Read(ref _suspensions) > 0;

    private static void ApplyAll()
    {
        SuspensionChanged?.Invoke(null, EventArgs.Empty);
        foreach (var layer in Layers.ToArray())
        {
            layer.ApplyAllHere();
        }
    }

    private sealed class Suspension : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Interlocked.Decrement(ref _suspensions) == 0)
            {
                ApplyAll();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!Layers.Contains(this))
        {
            Layers.Add(this);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Layers.Remove(this);
        base.OnDetachedFromVisualTree(e);
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
            _wanted[surface] = false;
            Apply(surface);
            return;
        }

        SetLeft(surface, bounds.X);
        SetTop(surface, bounds.Y);
        surface.Width = bounds.Width;
        surface.Height = bounds.Height;
        _wanted[surface] = true;
        Apply(surface);
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
            _wanted[surface] = false;
            Apply(surface);
        }
    }

    /// <summary>
    /// Gives a surface up, for good. Only the owner of the panel calls this, when
    /// the panel itself is gone.
    /// </summary>
    public void Release(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _wanted.Remove(surface);
        Children.Remove(surface);
    }

    /// <summary>
    /// A surface is on screen when its panel wants it there and nothing has
    /// asked the whole layer to stand down.
    /// </summary>
    private void Apply(Control surface) =>
        surface.IsVisible =
            _wanted.GetValueOrDefault(surface) && Volatile.Read(ref _suspensions) == 0;

    private void ApplyAllHere()
    {
        foreach (var surface in Children.ToArray())
        {
            Apply(surface);
        }
    }
}
