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

    public static readonly StyledProperty<bool> ShowHomeTabProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, bool>(nameof(ShowHomeTab));

    public static readonly StyledProperty<bool> IsHomeActiveProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, bool>(nameof(IsHomeActive));

    /// <summary>
    /// What has been open before, offered where it is reopened from rather than
    /// on a page of its own.
    /// </summary>
    public static readonly StyledProperty<IEnumerable?> RecentSessionsProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, IEnumerable?>(nameof(RecentSessions));

    public static readonly StyledProperty<bool> ShowHistoryTabProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, bool>(nameof(ShowHistoryTab));

    public RuntimeTabStripView()
    {
        InitializeComponent();
    }

    public IEnumerable? RecentSessions
    {
        get => GetValue(RecentSessionsProperty);
        set => SetValue(RecentSessionsProperty, value);
    }

    public bool ShowHistoryTab
    {
        get => GetValue(ShowHistoryTabProperty);
        set => SetValue(ShowHistoryTabProperty, value);
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

    public bool ShowHomeTab
    {
        get => GetValue(ShowHomeTabProperty);
        set => SetValue(ShowHomeTabProperty, value);
    }

    public bool IsHomeActive
    {
        get => GetValue(IsHomeActiveProperty);
        set => SetValue(IsHomeActiveProperty, value);
    }

    public string HomeItemStatus => IsHomeActive ? "Current tab" : string.Empty;

    public ScrollBarVisibility HorizontalScrollBars => Orientation == Orientation.Horizontal
        ? ScrollBarVisibility.Auto
        : ScrollBarVisibility.Disabled;

    public ScrollBarVisibility VerticalScrollBars => Orientation == Orientation.Horizontal
        ? ScrollBarVisibility.Disabled
        : ScrollBarVisibility.Auto;

    /// <summary>Raised when the strip's own add-a-tab control is pressed.</summary>
    public event EventHandler<RoutedEventArgs>? AddTabRequested;

    public event EventHandler<RoutedEventArgs>? HomeRequested;

    public event EventHandler<RoutedEventArgs>? RecentSessionRequested;

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
        }
        else if (change.Property == IsHomeActiveProperty)
        {
            RaisePropertyChanged(nameof(HomeItemStatus));
        }
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

    private void OnHome(object? sender, RoutedEventArgs e) =>
        HomeRequested?.Invoke(sender, e);

    /// <summary>
    /// The flyout closes itself: reopening something is a navigation, and a
    /// menu left standing over what it just opened has to be dismissed by hand.
    /// </summary>
    private void OnRecentSession(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            FlyoutBase.GetAttachedFlyout(HistoryTab)?.Hide();
            _ = control;
        }

        RecentSessionRequested?.Invoke(sender, e);
    }

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
