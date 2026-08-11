using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace GhostShell.App.Views.Components;

/// <summary>
/// The open runtime tabs. The shell hosts this at whichever edge the appearance
/// profile selects, so the strip is one control with one template rather than a
/// copy per edge. Every interaction is re-raised for the shell to handle, which
/// keeps tab activation, closing, and reordering owned in one place.
/// </summary>
public sealed partial class RuntimeTabStripView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> TabsProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, IEnumerable?>(nameof(Tabs));

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, Orientation>(
            nameof(Orientation),
            Orientation.Horizontal);

    /// <summary>
    /// Whether an overflowing strip draws a hairline after its add button.
    /// Only the title-bar strip wants it — that is the one place something
    /// else sits immediately beyond the strip's end.
    /// </summary>
    public static readonly StyledProperty<bool> ShowsOverflowSeparatorProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, bool>(nameof(ShowsOverflowSeparator));

    public bool ShowsOverflowSeparator
    {
        get => GetValue(ShowsOverflowSeparatorProperty);
        set => SetValue(ShowsOverflowSeparatorProperty, value);
    }

    public RuntimeTabStripView()
    {
        InitializeComponent();
        SyncScrollBars();
        SyncAddButtonDock();
        SizeChanged += (_, _) => UpdateOverflowFade();
    }

    public IEnumerable? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    /// <summary>
    /// Horizontal for a strip along the top or bottom, vertical for one docked to
    /// a side. The scroll bars follow, so the strip only ever scrolls along the
    /// axis it actually grows on.
    /// </summary>
    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>
    /// Hidden along the strip's axis (overflow shows as an edge fade, not a
    /// bar), Disabled across it. Assigned directly rather than bound: the
    /// consumer sets Orientation after this view's own bindings have read
    /// their value, and the refresh never reached them — which left a
    /// side-docked strip free to grow 13px wider than its viewport and clip
    /// its own close buttons.
    /// </summary>
    private void SyncScrollBars()
    {
        if (TabScrollViewer is null)
        {
            return;
        }

        var horizontal = Orientation == Orientation.Horizontal;
        TabScrollViewer.HorizontalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Hidden
            : ScrollBarVisibility.Disabled;
        TabScrollViewer.VerticalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Hidden;
    }

    /// <summary>Raised when the strip's own add-a-tab control is pressed.</summary>
    public event EventHandler<RoutedEventArgs>? AddTabRequested;

    public event EventHandler<RoutedEventArgs>? ActivateRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<DragEventArgs>? TabDragEnter;

    public event EventHandler<DragEventArgs>? TabDragLeave;

    public event EventHandler<DragEventArgs>? TabDragOver;

    public event EventHandler<DragEventArgs>? TabDrop;

    public event EventHandler<PointerCaptureLostEventArgs>? ReorderPointerCaptureLost;

    public event EventHandler<PointerEventArgs>? ReorderPointerMoved;

    public event EventHandler<PointerPressedEventArgs>? ReorderPointerPressed;

    public event EventHandler<PointerReleasedEventArgs>? ReorderPointerReleased;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OrientationProperty)
        {
            SyncScrollBars();
            SyncAddButtonDock();
            UpdateOverflowFade();
        }
        else if (change.Property == ShowsOverflowSeparatorProperty)
        {
            UpdateOverflowFade();
        }
    }

    /// <summary>The add button sits past the strip's growing end, outside the scroll.</summary>
    private void SyncAddButtonDock() =>
        DockPanel.SetDock(
            AddTabButton,
            Orientation == Orientation.Horizontal
                ? Avalonia.Controls.Dock.Right
                : Avalonia.Controls.Dock.Bottom);

    private void OnTabScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateOverflowFade();
    }

    /// <summary>
    /// Overflow announces itself as a fade: tabs dissolve at whichever edge
    /// more of them are hiding behind. At rest with everything visible there
    /// is no mask at all.
    /// </summary>
    private void UpdateOverflowFade()
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
        var fadeStart = offset > 1;
        var fadeEnd = extent - viewport - offset > 1;
        if (viewport <= 0 || (!fadeStart && !fadeEnd))
        {
            TabScrollViewer.OpacityMask = null;
            return;
        }

        // A soft, eased dissolve rather than a linear wipe: the ramp follows a
        // smoothstep curve sampled into stops, so tabs melt away instead of
        // hitting a visible gradient edge.
        var fade = Math.Min(56, viewport / 3) / viewport;
        var samples = new List<Avalonia.Media.GradientStop>();
        const int sampleCount = 6;
        for (var i = 0; i <= sampleCount; i++)
        {
            var t = (double)i / sampleCount;
            var eased = t * t * (3 - (2 * t));
            var alpha = (byte)Math.Round(eased * byte.MaxValue);
            var colour = Avalonia.Media.Color.FromArgb(alpha, 0, 0, 0);
            if (fadeStart)
            {
                samples.Add(new Avalonia.Media.GradientStop(colour, t * fade));
            }

            if (fadeEnd)
            {
                samples.Add(new Avalonia.Media.GradientStop(colour, 1 - (t * fade)));
            }
        }

        if (!fadeStart)
        {
            samples.Add(new Avalonia.Media.GradientStop(Avalonia.Media.Colors.Black, 0));
        }

        if (!fadeEnd)
        {
            samples.Add(new Avalonia.Media.GradientStop(Avalonia.Media.Colors.Black, 1));
        }

        var stops = new Avalonia.Media.GradientStops();
        foreach (var stop in samples.OrderBy(candidate => candidate.Offset))
        {
            stops.Add(stop);
        }

        TabScrollViewer.OpacityMask = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, 0, RelativeUnit.Relative)
                : new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = stops,
        };
    }

    private void RaisePropertyChanged(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    public new event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnAddTab(object? sender, RoutedEventArgs e) =>
        AddTabRequested?.Invoke(sender, e);

    private void OnActivate(object? sender, RoutedEventArgs e) =>
        ActivateRequested?.Invoke(sender, e);

    private void OnClose(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnDragEnter(object? sender, DragEventArgs e) =>
        TabDragEnter?.Invoke(sender, e);

    private void OnDragLeave(object? sender, DragEventArgs e) =>
        TabDragLeave?.Invoke(sender, e);

    private void OnDragOver(object? sender, DragEventArgs e) =>
        TabDragOver?.Invoke(sender, e);

    private void OnDrop(object? sender, DragEventArgs e) =>
        TabDrop?.Invoke(sender, e);

    private void OnDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ReorderPointerCaptureLost?.Invoke(sender, e);

    private void OnDragPointerMoved(object? sender, PointerEventArgs e) =>
        ReorderPointerMoved?.Invoke(sender, e);

    private void OnDragPointerPressed(object? sender, PointerPressedEventArgs e) =>
        ReorderPointerPressed?.Invoke(sender, e);

    private void OnDragPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ReorderPointerReleased?.Invoke(sender, e);
}
