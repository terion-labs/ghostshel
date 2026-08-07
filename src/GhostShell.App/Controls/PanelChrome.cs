using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model;
using Dock.Model.Core;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Controls;

/// <summary>
/// A runtime panel: its card, its header, and the actions every panel has.
///
/// There used to be eight of these, one per panel kind, each spelling out the
/// same header by hand — a card, a status dot, a drag handle round the title,
/// then split, split, close. They had drifted apart in every way that is easy to
/// get wrong and hard to see: the browser's split buttons were declared without a
/// <c>Grid.Column</c> and so rendered stacked under its status dot, invisible;
/// its split icons were the other way round from the other seven; header heights
/// were 36 in six views and 40 in two.
///
/// None of that was a decision. It was the same intent written eight times. This
/// is that intent once: a panel says what is particular to it — its status, its
/// own controls, what closing it is called — and the chrome it shares with every
/// other panel comes with the component.
/// </summary>
internal sealed class PanelChrome : ContentControl
{
    /// <summary>
    /// What the header calls this panel. Also the drag surface: the title is
    /// Dock's grab handle, so panels have no second tab strip above them.
    /// </summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(Title));

    /// <summary>Whether the panel is showing alone, filling its tab.</summary>
    public static readonly StyledProperty<bool> IsZoomedProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(IsZoomed));

    /// <summary>Whether this is the panel the shell is acting on.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(IsActive));

    /// <summary>
    /// The panel's liveness, drawn at the far left of the header. A dot for most,
    /// but the shape is the panel's to choose — what "live" means differs.
    /// </summary>
    public static readonly StyledProperty<object?> StatusProperty =
        AvaloniaProperty.Register<PanelChrome, object?>(nameof(Status));

    /// <summary>
    /// What the panel is pointed at, offered before its title — the connection
    /// selector, for the panels that have one.
    /// </summary>
    public static readonly StyledProperty<object?> LeadingProperty =
        AvaloniaProperty.Register<PanelChrome, object?>(nameof(Leading));

    /// <summary>
    /// Controls only this panel has, given the header's free width: the browser's
    /// address bar and its navigation. Where this is empty the title takes the
    /// space instead, so the whole row stays draggable.
    /// </summary>
    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<PanelChrome, object?>(nameof(HeaderContent));

    /// <summary>
    /// The panel's own actions and state, placed before the shared ones so the
    /// split and close buttons stay in the same corner in every panel.
    /// </summary>
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PanelChrome, object?>(nameof(Actions));

    /// <summary>
    /// What closing this panel is called, for the tooltip and for screen readers.
    /// </summary>
    public static readonly StyledProperty<string> CloseLabelProperty =
        AvaloniaProperty.Register<PanelChrome, string>(
            nameof(CloseLabel),
            "Close panel");

    /// <summary>
    /// Whether this panel can be split. Off only where placing a second panel
    /// beside this one is meaningless.
    /// </summary>
    public static readonly StyledProperty<bool> CanSplitProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(CanSplit), true);

    /// <summary>
    /// How many columns the title spans — two when the panel adds no header
    /// controls, so the drag surface is the whole free width rather than just the
    /// glyphs of the title.
    /// </summary>
    public static readonly DirectProperty<PanelChrome, int> TitleColumnSpanProperty =
        AvaloniaProperty.RegisterDirect<PanelChrome, int>(
            nameof(TitleColumnSpan),
            chrome => chrome.TitleColumnSpan);

    /// <summary>Whether the panel's liveness mark has the room to be shown.</summary>
    public static readonly DirectProperty<PanelChrome, bool> IsStatusVisibleProperty =
        AvaloniaProperty.RegisterDirect<PanelChrome, bool>(
            nameof(IsStatusVisible),
            chrome => chrome.IsStatusVisible);

    /// <summary>Whether what the panel is pointed at has the room to be shown.</summary>
    public static readonly DirectProperty<PanelChrome, bool> IsLeadingVisibleProperty =
        AvaloniaProperty.RegisterDirect<PanelChrome, bool>(
            nameof(IsLeadingVisible),
            chrome => chrome.IsLeadingVisible);

    /// <summary>Whether the panel's own header controls have the room to be shown.</summary>
    public static readonly DirectProperty<PanelChrome, bool> IsHeaderContentVisibleProperty =
        AvaloniaProperty.RegisterDirect<PanelChrome, bool>(
            nameof(IsHeaderContentVisible),
            chrome => chrome.IsHeaderContentVisible);

    /// <summary>Whether the panel's own state and actions have the room to be shown.</summary>
    public static readonly DirectProperty<PanelChrome, bool> IsActionsVisibleProperty =
        AvaloniaProperty.RegisterDirect<PanelChrome, bool>(
            nameof(IsActionsVisible),
            chrome => chrome.IsActionsVisible);

    /// <summary>
    /// Whether this panel is in a window of its own.
    ///
    /// Read from the dock graph rather than tracked, so a panel dragged out by
    /// Dock's own drag says the same thing as one sent out by the header button.
    /// </summary>
    public static readonly DirectProperty<PanelChrome, bool> IsFloatingProperty =
        AvaloniaProperty.RegisterDirect<PanelChrome, bool>(
            nameof(IsFloating),
            chrome => chrome.IsFloating);

    /// <summary>
    /// Whether this panel may leave the workspace. Off for panels a layout pins in
    /// place, while the panel is not attached to a dock graph, and while it is
    /// already floating — there the header offers the way back instead.
    /// </summary>
    public static readonly DirectProperty<PanelChrome, bool> CanFloatProperty =
        AvaloniaProperty.RegisterDirect<PanelChrome, bool>(
            nameof(CanFloat),
            chrome => chrome.CanFloat);

    /// <summary>
    /// The widths below which each part of the header stops being drawn.
    ///
    /// A panel can be dragged narrower than its header needs, and something has to
    /// give. Left to itself a layout answers that by overlapping — the browser's
    /// status text printed straight through its own address box, and its title
    /// through the connection name beside it — because a control asked for a width
    /// nobody had. Naming the order in which the row gives things up is the
    /// difference between a header that degrades and one that becomes unreadable.
    ///
    /// Split, close, and the title stay at every width: those are how the panel is
    /// closed and how it is dragged, and a panel too narrow to close is a trap.
    /// </summary>
    private const double ActionsFloor = 360;
    private const double HeaderContentFloor = 260;
    private const double LeadingFloor = 210;
    private const double StatusFloor = 170;

    private int _titleColumnSpan = 2;
    private bool _isStatusVisible;
    private bool _isLeadingVisible;
    private bool _isHeaderContentVisible;
    private bool _isActionsVisible;
    private bool _isFloating;
    private bool _canFloat;
    private Button? _float;
    private Button? _dock;
    private Button? _splitLeftRight;
    private Button? _splitTopBottom;
    private Button? _close;

    static PanelChrome()
    {
        foreach (var slot in new[]
                 {
                     StatusProperty,
                     LeadingProperty,
                     HeaderContentProperty,
                     ActionsProperty,
                 })
        {
            slot.Changed.AddClassHandler<PanelChrome>(
                (chrome, _) => chrome.UpdateSlotVisibility());
        }
    }

    /// <summary>Raised when the user asks for this panel to be closed.</summary>
    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>
    /// Raised when the user asks for an empty panel beside this one. What it
    /// becomes is chosen there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsZoomed
    {
        get => GetValue(IsZoomedProperty);
        set => SetValue(IsZoomedProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public object? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public object? Leading
    {
        get => GetValue(LeadingProperty);
        set => SetValue(LeadingProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public string CloseLabel
    {
        get => GetValue(CloseLabelProperty);
        set => SetValue(CloseLabelProperty, value);
    }

    public bool CanSplit
    {
        get => GetValue(CanSplitProperty);
        set => SetValue(CanSplitProperty, value);
    }

    public int TitleColumnSpan
    {
        get => _titleColumnSpan;
        private set => SetAndRaise(TitleColumnSpanProperty, ref _titleColumnSpan, value);
    }

    public bool IsStatusVisible
    {
        get => _isStatusVisible;
        private set => SetAndRaise(IsStatusVisibleProperty, ref _isStatusVisible, value);
    }

    public bool IsLeadingVisible
    {
        get => _isLeadingVisible;
        private set => SetAndRaise(IsLeadingVisibleProperty, ref _isLeadingVisible, value);
    }

    public bool IsHeaderContentVisible
    {
        get => _isHeaderContentVisible;
        private set => SetAndRaise(
            IsHeaderContentVisibleProperty,
            ref _isHeaderContentVisible,
            value);
    }

    public bool IsActionsVisible
    {
        get => _isActionsVisible;
        private set => SetAndRaise(IsActionsVisibleProperty, ref _isActionsVisible, value);
    }

    public bool IsFloating
    {
        get => _isFloating;
        private set => SetAndRaise(IsFloatingProperty, ref _isFloating, value);
    }

    public bool CanFloat
    {
        get => _canFloat;
        private set => SetAndRaise(CanFloatProperty, ref _canFloat, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        UpdateSlotVisibility(arranged.Width);
        return arranged;
    }

    /// <summary>
    /// A panel arriving in a new window is a new visual tree, so this is where
    /// floating is noticed — whichever way the panel got there.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateDockState();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        Detach(_float, OnFloatClick);
        Detach(_dock, OnDockClick);
        Detach(_splitLeftRight, OnSplitLeftRightClick);
        Detach(_splitTopBottom, OnSplitTopBottomClick);
        Detach(_close, OnCloseClick);

        _float = e.NameScope.Find<Button>("PART_Float");
        _dock = e.NameScope.Find<Button>("PART_Dock");
        _splitLeftRight = e.NameScope.Find<Button>("PART_SplitLeftRight");
        _splitTopBottom = e.NameScope.Find<Button>("PART_SplitTopBottom");
        _close = e.NameScope.Find<Button>("PART_Close");

        Attach(_float, OnFloatClick);
        Attach(_dock, OnDockClick);
        Attach(_splitLeftRight, OnSplitLeftRightClick);
        Attach(_splitTopBottom, OnSplitTopBottomClick);
        Attach(_close, OnCloseClick);
        UpdateDockState();
    }

    private static void Attach(Button? button, EventHandler<RoutedEventArgs> handler)
    {
        if (button is not null)
        {
            button.Click += handler;
        }
    }

    private static void Detach(Button? button, EventHandler<RoutedEventArgs> handler)
    {
        if (button is not null)
        {
            button.Click -= handler;
        }
    }

    /// <summary>
    /// The chrome raises panel actions as itself, not as the button that was
    /// pressed. The shell resolves which panel to act on from the sender's data
    /// context, and the chrome's is the panel — a button inside a template is an
    /// implementation detail the shell should never have to reach through.
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        CloseRequested?.Invoke(this, e);
    }

    private void OnSplitLeftRightClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SplitRequested?.Invoke(this, PanelSplitOrientation.LeftRight);
    }

    private void OnSplitTopBottomClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SplitRequested?.Invoke(this, PanelSplitOrientation.TopBottom);
    }

    /// <summary>
    /// Sends the panel out into a window of its own.
    ///
    /// This used to be a double-click on the title, which is not a thing anyone
    /// discovers and was not a thing anyone could undo — Dock floats a dockable
    /// but has no matching gesture for putting it back, so a panel that left the
    /// workspace by accident stayed out. Both directions are stated in the header
    /// now, in the same place as split and close.
    /// </summary>
    private void OnFloatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        if (ResolveDockable() is not { Owner: IDock { Factory: { } factory } } dockable
            || !CanFloatDockable(dockable))
        {
            return;
        }

        factory.FloatDockable(dockable);
        factory.ActivateWindow(dockable);
        e.Handled = true;
        UpdateDockState();
    }

    private void OnDockClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        if (ResolveDockable() is not { Owner: IDock { Factory: RuntimeDockFactory factory } }
            dockable)
        {
            return;
        }

        if (factory.DockBack(dockable))
        {
            e.Handled = true;
        }

        UpdateDockState();
    }

    /// <summary>
    /// The panel this chrome dresses, as the dock graph knows it. The chrome's own
    /// data context is the panel's view model; the dockable is the control Dock
    /// puts around it.
    /// </summary>
    private IDockable? ResolveDockable() =>
        this.FindAncestorOfType<DockableControl>()?.DataContext as IDockable;

    private static bool CanFloatDockable(IDockable dockable) =>
        DockCapabilityResolver.IsEnabled(
            dockable,
            DockCapability.Float,
            DockCapabilityResolver.ResolveOperationDock(dockable));

    private void UpdateDockState()
    {
        if (ResolveDockable() is not { Owner: IDock { Factory: { } factory } } dockable)
        {
            CanFloat = false;
            IsFloating = false;
            return;
        }

        IsFloating = factory is RuntimeDockFactory runtime && runtime.IsFloating(dockable);
        // The two are one control in two states: a panel already in its own window
        // is offered the way back instead of the way out.
        CanFloat = !IsFloating && CanFloatDockable(dockable);
    }

    private void UpdateSlotVisibility() => UpdateSlotVisibility(Bounds.Width);

    private void UpdateSlotVisibility(double width)
    {
        // Before the first arrange there is no width to judge by, and hiding
        // everything until one arrives would make the header flash empty.
        var available = width > 0 ? width : double.PositiveInfinity;
        IsStatusVisible = Status is not null && available >= StatusFloor;
        IsLeadingVisible = Leading is not null && available >= LeadingFloor;
        IsHeaderContentVisible =
            HeaderContent is not null && available >= HeaderContentFloor;
        IsActionsVisible = Actions is not null && available >= ActionsFloor;
        TitleColumnSpan = IsHeaderContentVisible ? 1 : 2;
    }
}
