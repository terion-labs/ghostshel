using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace GhostShell.App.Controls;

/// <summary>
/// Opts a control into having its corners worked out from the one it sits
/// inside.
///
/// Nested rounded rectangles only look like one surface when their curves
/// share a centre, which means the inner radius is the outer radius less the
/// distance between them. Apple states that rule and, from macOS 26, applies
/// it for you through <c>ConcentricRectangle</c> and <c>containerShape</c>.
/// Avalonia has no equivalent, so this is that rule rather than a port of
/// their implementation, which is not published.
///
/// An attached property rather than a border subclass, because Avalonia's type
/// selectors match one type exactly: a <c>Border.FloatingSidebar</c> style
/// stops applying the moment the element is a subclass of Border, and a
/// sidebar that quietly loses its background and margin does not look like a
/// styling rule — it looks like it vanished. Opting in by property leaves the
/// element the type its styles were written against.
///
/// A container is marked with <see cref="IsContainerProperty"/> — or is simply
/// the nearest ancestor that carries a radius. The distance is measured from
/// the arranged bounds rather than added up from margins and padding, so it
/// stays right whatever put the gap there.
///
/// The corners are still circular arcs. Apple's are continuous curvature —
/// squircles — which no <see cref="Border"/> can draw, and which would need
/// the fill, the stroke and the clip all replaced by geometry. The
/// relationships are what read as wrong when they are wrong; the curve is a
/// separate question, and a smaller one below about twelve points.
/// </summary>
public static class Concentric
{
    /// <summary>Whether this element works its corners out from its container.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsEnabled",
            typeof(Concentric));

    /// <summary>
    /// Marks an element as the shape inner corners are measured against.
    /// </summary>
    public static readonly AttachedProperty<bool> IsContainerProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>(
            "IsContainer",
            typeof(Concentric));

    /// <summary>
    /// The radius the container is drawn with, for a container that does not
    /// carry one itself — a window's frame, drawn by the platform.
    /// </summary>
    public static readonly AttachedProperty<double> ContainerRadiusProperty =
        AvaloniaProperty.RegisterAttached<Visual, double>(
            "ContainerRadius",
            typeof(Concentric));

    /// <summary>
    /// How tight a derived corner may become. Below this a corner reads as
    /// square, and squaring one corner of a rounded thing looks like a mistake
    /// rather than a decision.
    /// </summary>
    public static readonly AttachedProperty<double> MinimumRadiusProperty =
        AvaloniaProperty.RegisterAttached<Control, double>(
            "MinimumRadius",
            typeof(Concentric),
            defaultValue: 2);

    private static readonly AttachedProperty<ConcentricCornerReconciler?> ReconcilerProperty =
        AvaloniaProperty.RegisterAttached<Control, ConcentricCornerReconciler?>(
            "Reconciler",
            typeof(Concentric));

    static Concentric() =>
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);

    public static bool GetIsEnabled(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(Control element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
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

    public static double GetMinimumRadius(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(MinimumRadiusProperty);
    }

    public static void SetMinimumRadius(Control element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(MinimumRadiusProperty, value);
    }

    private static void OnIsEnabledChanged(Control element, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.GetNewValue<bool>())
        {
            if (element.GetValue(ReconcilerProperty) is not null)
            {
                return;
            }

            var reconciler = new ConcentricCornerReconciler(
                element,
                GetMinimumRadius(element));
            element.SetValue(ReconcilerProperty, reconciler);
            element.LayoutUpdated += reconciler.OnLayoutUpdated;
            return;
        }

        if (element.GetValue(ReconcilerProperty) is not { } existing)
        {
            return;
        }

        element.LayoutUpdated -= existing.OnLayoutUpdated;
        element.SetValue(ReconcilerProperty, null);
    }
}

/// <summary>
/// Keeps one control's corners answering to the surface it sits inside.
///
/// Owned by the control rather than applied to it, so the theme's own value
/// stays reachable: the rule needs it both as the answer for corners that are
/// not concentric and as the thing to fall back to when it stops applying at
/// all.
/// </summary>
internal sealed class ConcentricCornerReconciler(Control owner, double minimumRadius)
{
    public void OnLayoutUpdated(object? sender, EventArgs args) => Reconcile();

    public void Reconcile()
    {
        // Back to the theme's value before reading it. The corners that are
        // not concentric fall back to what the element was given, and what it
        // was given is a style — not whatever this worked out last time, which
        // is what it would read if the value it wrote were left in place.
        owner.ClearValue(Border.CornerRadiusProperty);
        var authored = owner.GetValue(Border.CornerRadiusProperty);

        var derived = ConcentricCorners.DeriveFor(owner, minimumRadius);
        if (derived is { } radius && !radius.Equals(authored))
        {
            owner.SetValue(Border.CornerRadiusProperty, radius);
        }
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

        return Derive(
            outer,
            container.Bounds.Size,
            offset,
            element.Bounds.Size,
            minimumRadius);
    }

    private static (Visual? Container, double Radius) FindContainer(Control element)
    {
        foreach (var ancestor in element.GetVisualAncestors())
        {
            if (Concentric.GetIsContainer(ancestor))
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
        var declared = Concentric.GetContainerRadius(element);
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

        // The shortest way to the container, whichever side that is.
        //
        // Corner by corner reads wrong on anything tall or wide: a sidebar
        // below a tab strip is far from the window's top corners and hard
        // against its bottom ones, and giving it two tight corners and two
        // round ones makes one shape look like two. It is one surface sitting
        // one distance inside another, so it takes one radius — the one that
        // its closest edge earns.
        var gap = Math.Max(0, Math.Min(
            Math.Min(offsetInContainer.X, offsetInContainer.Y),
            Math.Min(
                containerSize.Width - (offsetInContainer.X + size.Width),
                containerSize.Height - (offsetInContainer.Y + size.Height))));

        // Further out than the radius itself is not near the container's
        // corners at all, and stepping in by that distance would square
        // something meant to be round. There it keeps what it was given.
        return gap >= outerRadius
            ? null
            : new CornerRadius(Math.Max(minimumRadius, outerRadius - gap));
    }
}
