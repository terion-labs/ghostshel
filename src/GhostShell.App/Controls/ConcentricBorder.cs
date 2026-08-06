using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace GhostShell.App.Controls;

/// <summary>
/// A border whose corners are worked out from the one it sits inside.
///
/// Nested rounded rectangles only look like one surface when their curves
/// share a centre, which means the inner radius is the outer radius less the
/// distance between them. Apple states that rule and, from macOS 26, applies
/// it for you through <c>ConcentricRectangle</c> and <c>containerShape</c>.
/// Avalonia has no equivalent, so this is that rule rather than a port of
/// their implementation, which is not published.
///
/// A container is marked with <see cref="ConcentricBorder.IsContainerProperty"/>
/// — or is simply the nearest ancestor that is one of these. The distance is
/// measured from the arranged bounds rather than added up from margins and
/// padding, so it stays right whatever put the gap there.
///
/// The corners are still circular arcs. Apple's are continuous curvature —
/// squircles — which no <see cref="Border"/> can draw, and which would need
/// the fill, the stroke and the clip all replaced by geometry. The
/// relationships are what read as wrong when they are wrong; the curve is a
/// separate question, and a smaller one below about twelve points.
/// </summary>
public sealed class ConcentricBorder : Border
{
    /// <summary>
    /// Marks an element as the shape inner borders measure themselves against.
    /// </summary>
    public static readonly AttachedProperty<bool> IsContainerProperty =
        AvaloniaProperty.RegisterAttached<ConcentricBorder, Visual, bool>("IsContainer");

    /// <summary>
    /// The radius the container is drawn with. Set on the container alongside
    /// <see cref="IsContainerProperty"/> when it is not itself a border.
    /// </summary>
    public static readonly AttachedProperty<double> ContainerRadiusProperty =
        AvaloniaProperty.RegisterAttached<ConcentricBorder, Visual, double>("ContainerRadius");

    /// <summary>
    /// How tight a derived corner may become. Below this a corner reads as
    /// square, and squaring one corner of a rounded thing looks like a mistake
    /// rather than a decision.
    /// </summary>
    public static readonly StyledProperty<double> MinimumRadiusProperty =
        AvaloniaProperty.Register<ConcentricBorder, double>(nameof(MinimumRadius), 2);

    private readonly ConcentricCornerReconciler _corners;

    public ConcentricBorder()
    {
        _corners = new ConcentricCornerReconciler(this, MinimumRadius);
        LayoutUpdated += (_, _) => Reconcile();
    }

    public static bool GetIsContainer(Visual element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsContainerProperty);
    }

    public static void SetIsContainer(Visual element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsContainerProperty, value);
    }

    public static double GetContainerRadius(Visual element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ContainerRadiusProperty);
    }

    public static void SetContainerRadius(Visual element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ContainerRadiusProperty, value);
    }

    public double MinimumRadius
    {
        get => GetValue(MinimumRadiusProperty);
        set => SetValue(MinimumRadiusProperty, value);
    }

    private void Reconcile() => _corners.Reconcile();
}

/// <summary>
/// Keeps one control's corners answering to the surface it sits inside.
///
/// Owned by the control rather than applied to it, because it has to remember
/// whether it set the radius: when the rule stops applying — the element moves
/// away from the corner, or the surface it was measuring against goes square —
/// the value it wrote has to be given back rather than left behind.
/// </summary>
internal sealed class ConcentricCornerReconciler(Control owner, double minimumRadius)
{
    private bool _applied;

    public void Reconcile()
    {
        var derived = ConcentricCorners.DeriveFor(owner, minimumRadius);
        if (derived is { } radius)
        {
            _applied = true;
            if (!radius.Equals(owner.GetValue(Border.CornerRadiusProperty)))
            {
                owner.SetValue(Border.CornerRadiusProperty, radius);
            }

            return;
        }

        if (!_applied)
        {
            return;
        }

        // Back to whatever the theme says, not to whatever was last worked out.
        _applied = false;
        owner.ClearValue(Border.CornerRadiusProperty);
    }
}

/// <summary>
/// The rule itself, apart from any control: an inner radius is its container's
/// less the distance between them, so the two curves share a centre.
/// </summary>
public static class ConcentricCorners
{
    /// <summary>
    /// Works the radius out for a control from the nearest rounded surface it
    /// sits inside. Null when there is no such surface, or when the rule does
    /// not apply to where the control sits in it.
    /// </summary>
    public static CornerRadius? DeriveFor(Control element, double minimumRadius)
    {
        ArgumentNullException.ThrowIfNull(element);
        var (container, outer) = FindContainer(element);
        if (container is null
            || element.TranslatePoint(default, container) is not { } offset)
        {
            return null;
        }

        return Derive(outer, container.Bounds.Size, offset, element.Bounds.Size, minimumRadius);
    }

    private static (Visual? Container, double Radius) FindContainer(Control element)
    {
        foreach (var ancestor in element.GetVisualAncestors())
        {
            if (ConcentricBorder.GetIsContainer(ancestor))
            {
                return (ancestor, LargestCorner(ancestor));
            }

            if (ancestor is Border { CornerRadius: var corner } && corner != default)
            {
                return (ancestor, LargestCorner(ancestor));
            }

            if (ancestor is TemplatedControl { CornerRadius: var themed } && themed != default)
            {
                return (ancestor, LargestCorner(ancestor));
            }
        }

        return (null, 0);
    }

    private static double LargestCorner(Visual element)
    {
        var declared = ConcentricBorder.GetContainerRadius(element);
        if (declared > 0)
        {
            return declared;
        }

        var corner = element switch
        {
            Border border => border.CornerRadius,
            TemplatedControl templated => templated.CornerRadius,
            _ => default,
        };

        return Math.Max(
            Math.Max(corner.TopLeft, corner.TopRight),
            Math.Max(corner.BottomLeft, corner.BottomRight));
    }

    /// <summary>
    /// Returns null when there is nothing to derive from — an unarranged
    /// element, or a container with square corners, where guessing would be
    /// worse than leaving the radius alone.
    /// </summary>
    public static CornerRadius? Derive(
        double outerRadius,
        Size containerSize,
        Point offsetInContainer,
        Size size,
        double minimumRadius)
    {
        if (outerRadius <= 0
            || size.Width <= 0
            || size.Height <= 0
            || containerSize.Width <= 0
            || containerSize.Height <= 0)
        {
            return null;
        }

        var left = offsetInContainer.X;
        var top = offsetInContainer.Y;
        var right = containerSize.Width - (offsetInContainer.X + size.Width);
        var bottom = containerSize.Height - (offsetInContainer.Y + size.Height);

        // An element only shares its container's corners if it is inside all
        // four of them. A notice pinned to one corner of a panel is nowhere
        // near the other three, and stepping in by those distances would floor
        // every corner and quietly square something meant to be round. Where
        // the rule does not apply, it declines rather than guessing.
        if (left < 0 || top < 0 || right < 0 || bottom < 0
            || left >= outerRadius || top >= outerRadius
            || right >= outerRadius || bottom >= outerRadius)
        {
            return null;
        }

        return new CornerRadius(
            Step(outerRadius, left, top, minimumRadius),
            Step(outerRadius, right, top, minimumRadius),
            Step(outerRadius, right, bottom, minimumRadius),
            Step(outerRadius, left, bottom, minimumRadius));
    }

    /// <summary>
    /// Where the two edges meeting at a corner are inset by different amounts
    /// the curves cannot truly share a centre, because a corner carries one
    /// radius and not two. The smaller distance wins: a corner that is too
    /// tight reads as deliberate, one that is too round bulges past the shape
    /// it is meant to sit inside.
    /// </summary>
    private static double Step(
        double outer,
        double first,
        double second,
        double minimum) =>
        Math.Max(minimum, outer - Math.Max(0, Math.Min(first, second)));
}
