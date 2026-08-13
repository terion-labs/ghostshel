using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GhostShell.App.Views.Components;

public sealed partial class RuntimeTabStripView
{
    private INotifyCollectionChanged? _observedCollection;
    private readonly List<INotifyPropertyChanged> _observedTabs = [];

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopObservingTabs();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveTabs(Tabs);
    }

    private void OnTabScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateOverflowPresentation();
    }

    private void ObserveTabs(IEnumerable? tabs)
    {
        StopObservingTabs();
        _observedCollection = tabs as INotifyCollectionChanged;
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged += OnTabsCollectionChanged;
        }

        if (tabs is not null)
        {
            _observedTabs.AddRange(tabs.OfType<INotifyPropertyChanged>());
            foreach (var tab in _observedTabs)
            {
                tab.PropertyChanged += OnTabPropertyChanged;
            }
        }

        QueueOverflowPresentation();
    }

    private void StopObservingTabs()
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnTabsCollectionChanged;
            _observedCollection = null;
        }

        foreach (var tab in _observedTabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _observedTabs.Clear();
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        ObserveTabs(Tabs);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is null or "IsActive" or "Title" or "IconSymbol")
        {
            QueueOverflowPresentation();
        }
    }

    private void QueueOverflowPresentation() =>
        Dispatcher.UIThread.Post(UpdateOverflowPresentation, DispatcherPriority.Loaded);

    private void UpdateOverflowPresentation()
    {
        var pinnedEdges = PinActiveTab();
        UpdateOverflowFade(pinnedEdges.Leading, pinnedEdges.Trailing);
    }

    /// <summary>
    /// The selected tab remains reachable while the overflow moves. A render
    /// transform deliberately leaves its layout slot behind, so neighbouring
    /// tabs travel underneath the raised active chip instead of being reflowed.
    /// The generated presenter is the StackPanel child, so it owns both the
    /// transform and z-index; raising the template's inner grid cannot reorder
    /// it against sibling presenters.
    /// </summary>
    private (bool Leading, bool Trailing) PinActiveTab()
    {
        var tabHosts = this.GetVisualDescendants()
            .OfType<Grid>()
            .Where(grid => grid.Classes.Contains("RuntimeTabDropTarget"))
            .ToArray();
        foreach (var host in tabHosts)
        {
            var container = host.FindAncestorOfType<ContentPresenter>();
            if (container is not null)
            {
                container.RenderTransform = null;
                container.ZIndex = 0;
            }
        }

        var active = tabHosts.SingleOrDefault(host => host.Classes.Contains("active"));
        var activeContainer = active?.FindAncestorOfType<ContentPresenter>();
        if (activeContainer is null)
        {
            return default;
        }

        activeContainer.ZIndex = 1;
        if (TabScrollViewer.Viewport is { Width: <= 0 } or { Height: <= 0 })
        {
            return default;
        }

        var topLeft = activeContainer.TranslatePoint(default, TabScrollViewer);
        var bottomRight = activeContainer.TranslatePoint(
            new Point(activeContainer.Bounds.Width, activeContainer.Bounds.Height),
            TabScrollViewer);
        if (topLeft is null || bottomRight is null)
        {
            return default;
        }

        var horizontal = Orientation == Orientation.Horizontal;
        var leading = horizontal ? topLeft.Value.X : topLeft.Value.Y;
        var trailing = horizontal ? bottomRight.Value.X : bottomRight.Value.Y;
        var viewport = horizontal
            ? TabScrollViewer.Viewport.Width
            : TabScrollViewer.Viewport.Height;
        var translation = leading < 0
            ? -leading
            : trailing > viewport
                ? viewport - trailing
                : 0;
        if (Math.Abs(translation) < 0.5)
        {
            return default;
        }

        activeContainer.RenderTransform = horizontal
            ? new TranslateTransform(translation, 0)
            : new TranslateTransform(0, translation);
        return (translation > 0, translation < 0);
    }

    /// <summary>
    /// Overflow announces itself as a fade: tabs dissolve at whichever edge
    /// more of them are hiding behind. At rest with everything visible there
    /// is no mask at all.
    /// </summary>
    private void UpdateOverflowFade(bool activePinnedLeading, bool activePinnedTrailing)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var extent = horizontal
            ? TabScrollViewer.Extent.Width
            : TabScrollViewer.Extent.Height;
        var viewport = horizontal
            ? TabScrollViewer.Viewport.Width
            : TabScrollViewer.Viewport.Height;
        var offset = horizontal ? TabScrollViewer.Offset.X : TabScrollViewer.Offset.Y;
        OverflowSeparator.IsVisible = ShowsOverflowSeparator
            && horizontal
            && extent - viewport > 1;
        var fadeStart = offset > 1 && !activePinnedLeading;
        var fadeEnd = extent - viewport - offset > 1 && !activePinnedTrailing;
        if (viewport <= 0 || (!fadeStart && !fadeEnd))
        {
            TabScrollViewer.OpacityMask = null;
            return;
        }

        // A soft, eased dissolve rather than a linear wipe: the ramp follows a
        // smoothstep curve sampled into stops, so tabs melt away instead of
        // hitting a visible gradient edge.
        var fade = Math.Min(56, viewport / 3) / viewport;
        var samples = new List<GradientStop>();
        const int sampleCount = 6;
        for (var i = 0; i <= sampleCount; i++)
        {
            var t = (double)i / sampleCount;
            var eased = t * t * (3 - (2 * t));
            var alpha = (byte)Math.Round(eased * byte.MaxValue);
            var colour = Color.FromArgb(alpha, 0, 0, 0);
            if (fadeStart)
            {
                samples.Add(new GradientStop(colour, t * fade));
            }

            if (fadeEnd)
            {
                samples.Add(new GradientStop(colour, 1 - (t * fade)));
            }
        }

        if (!fadeStart)
        {
            samples.Add(new GradientStop(Colors.Black, 0));
        }

        if (!fadeEnd)
        {
            samples.Add(new GradientStop(Colors.Black, 1));
        }

        var stops = new GradientStops();
        foreach (var stop in samples.OrderBy(candidate => candidate.Offset))
        {
            stops.Add(stop);
        }

        TabScrollViewer.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, 0, RelativeUnit.Relative)
                : new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = stops,
        };
    }
}
