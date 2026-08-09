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

    public RuntimeTabStripView()
    {
        InitializeComponent();
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

    // Hidden, not Auto: the strip still scrolls along its axis, but overflow
    // shows as an edge fade rather than as a scrollbar under the tabs.
    public ScrollBarVisibility HorizontalScrollBars => Orientation == Orientation.Horizontal
        ? ScrollBarVisibility.Hidden
        : ScrollBarVisibility.Disabled;

    public ScrollBarVisibility VerticalScrollBars => Orientation == Orientation.Horizontal
        ? ScrollBarVisibility.Disabled
        : ScrollBarVisibility.Hidden;

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
            RaisePropertyChanged(
                nameof(HorizontalScrollBars),
                nameof(VerticalScrollBars));
            SyncAddButtonDock();
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
        var fadeStart = offset > 1;
        var fadeEnd = extent - viewport - offset > 1;
        if (viewport <= 0 || (!fadeStart && !fadeEnd))
        {
            TabScrollViewer.OpacityMask = null;
            return;
        }

        var fade = Math.Min(28, viewport / 4) / viewport;
        TabScrollViewer.OpacityMask = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, 0, RelativeUnit.Relative)
                : new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new Avalonia.Media.GradientStop(
                    fadeStart
                        ? Avalonia.Media.Colors.Transparent
                        : Avalonia.Media.Colors.Black,
                    0),
                new Avalonia.Media.GradientStop(Avalonia.Media.Colors.Black, fadeStart ? fade : 0),
                new Avalonia.Media.GradientStop(Avalonia.Media.Colors.Black, fadeEnd ? 1 - fade : 1),
                new Avalonia.Media.GradientStop(
                    fadeEnd
                        ? Avalonia.Media.Colors.Transparent
                        : Avalonia.Media.Colors.Black,
                    1),
            },
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
