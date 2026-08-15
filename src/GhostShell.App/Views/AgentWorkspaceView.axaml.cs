using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace GhostShell.App.Views;

public sealed partial class AgentWorkspaceView : UserControl
{
    /// <summary>Small enough to tuck away, large enough to keep a whole conversation column.</summary>
    private const double MinimumFloatingWidth = 320;

    private const double MinimumFloatingHeight = 360;

    private static readonly Lazy<Cursor> HorizontalResizeCursor = new(
        () => new Cursor(StandardCursorType.SizeWestEast));

    private static readonly Lazy<Cursor> VerticalResizeCursor = new(
        () => new Cursor(StandardCursorType.SizeNorthSouth));

    private bool _wasFloating;
    private double? _floatingWidth;
    private double? _floatingHeight;
    private double? _dockedWidth;

    public AgentWorkspaceView()
    {
        InitializeComponent();
        AgentChatPromptInput.AddHandler(
            InputElement.KeyDownEvent,
            OnAgentPromptKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _wasFloating = Classes.Contains("floating");
        Classes.CollectionChanged += (_, _) =>
        {
            PreserveSizeAcrossPresentationChanges();
            UpdateResizeCursor();
        };
        UpdateResizeCursor();
    }

    private void PreserveSizeAcrossPresentationChanges()
    {
        var isFloating = Classes.Contains("floating");
        if (isFloating == _wasFloating)
        {
            return;
        }

        if (_wasFloating)
        {
            _floatingWidth = PreferredSize(Width, Bounds.Width);
            _floatingHeight = PreferredSize(Height, Bounds.Height);
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            if (_dockedWidth is { } dockedWidth)
            {
                Width = dockedWidth;
            }
        }
        else
        {
            _dockedWidth = PreferredSize(Width, Bounds.Width);
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            if (_floatingWidth is { } floatingWidth)
            {
                Width = floatingWidth;
            }

            if (_floatingHeight is { } floatingHeight)
            {
                Height = floatingHeight;
            }
        }

        _wasFloating = isFloating;
    }

    private static double? PreferredSize(double configured, double arranged) =>
        double.IsFinite(configured) && configured > 0
            ? configured
            : double.IsFinite(arranged) && arranged > 0
                ? arranged
                : null;

    /// <summary>
    /// A north-east/south-west double arrow, drawn: macOS exposes no diagonal
    /// resize cursor, so <c>StandardCursorType.BottomLeftCorner</c> falls back
    /// to a crosshair there. White under black keeps it legible on any
    /// surface. One per process — cursors are shared, not per-control.
    /// </summary>
    private static readonly Lazy<Cursor> DiagonalResizeCursor = new(() =>
    {
        const int size = 24;
        var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(size, size),
            new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var head = 5.0;
            var southWest = new Point(5, 19);
            var northEast = new Point(19, 5);
            foreach (var pen in new[]
            {
                new Pen(Brushes.White, 4.5, lineCap: PenLineCap.Round),
                new Pen(Brushes.Black, 2, lineCap: PenLineCap.Round),
            })
            {
                context.DrawLine(pen, southWest, northEast);
                context.DrawLine(pen, northEast, northEast + new Vector(-head, 0));
                context.DrawLine(pen, northEast, northEast + new Vector(0, head));
                context.DrawLine(pen, southWest, southWest + new Vector(head, 0));
                context.DrawLine(pen, southWest, southWest + new Vector(0, -head));
            }
        }

        return new Cursor(bitmap, new PixelPoint(size / 2, size / 2));
    });

    /// <summary>
    /// The flyout sits in the content zone's top-right corner, so the
    /// bottom-left grip grows it leftward and downward. Clamped to the
    /// hosting overlay, less this panel's own margins.
    /// </summary>
    private void OnFloatingResizeDragDelta(object? sender, VectorEventArgs e)
    {
        _ = sender;
        if (Parent is not Control host)
        {
            return;
        }

        var availableWidth = host.Bounds.Width - Margin.Left - Margin.Right;
        if (Classes.Contains("edgeResizable"))
        {
            var widthDelta = Classes.Contains("edgeLeft")
                ? e.Vector.X
                : -e.Vector.X;
            var width = Math.Clamp(
                Bounds.Width + widthDelta,
                MinimumFloatingWidth,
                Math.Max(MinimumFloatingWidth, availableWidth));
            Width = width;
            if (Classes.Contains("floating"))
            {
                _floatingWidth = width;
            }
            else
            {
                _dockedWidth = width;
            }

            return;
        }

        var availableHeight = host.Bounds.Height - Margin.Top - Margin.Bottom;
        _floatingWidth = Math.Clamp(
            Bounds.Width - e.Vector.X,
            MinimumFloatingWidth,
            Math.Max(MinimumFloatingWidth, availableWidth));
        _floatingHeight = Math.Clamp(
            Bounds.Height + e.Vector.Y,
            MinimumFloatingHeight,
            Math.Max(MinimumFloatingHeight, availableHeight));
        Width = _floatingWidth.Value;
        Height = _floatingHeight.Value;
    }

    private void UpdateResizeCursor() =>
        (FloatingResizeHandle.Cursor, FloatingHeightResizeHandle.Cursor) =
            Classes.Contains("edgeResizable")
                ? (HorizontalResizeCursor.Value, VerticalResizeCursor.Value)
                : (DiagonalResizeCursor.Value, VerticalResizeCursor.Value);

    private void OnFloatingHeightResizeDragDelta(object? sender, VectorEventArgs e)
    {
        _ = sender;
        if (Parent is not Control host || !Classes.Contains("edgeResizable"))
        {
            return;
        }

        var availableHeight = Math.Max(
            1,
            host.Bounds.Height - Margin.Top - Margin.Bottom);
        var minimumHeight = Math.Min(MinimumFloatingHeight, availableHeight);
        var heightDelta = Classes.Contains("anchorBottom")
            ? -e.Vector.Y
            : e.Vector.Y;
        _floatingHeight = Math.Clamp(
            Bounds.Height + heightDelta,
            minimumHeight,
            availableHeight);
        Height = _floatingHeight.Value;
    }

    public event EventHandler<RoutedEventArgs>? ApproveAgentActionRequested;

    public event EventHandler<KeyEventArgs>? AgentQuestionResponseKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? DeclineAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? DenyAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? DisableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentCapabilityAskRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? KeepAgentCapabilityOffRequested;

    public event EventHandler<RoutedEventArgs>? LoadOlderAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? StartNewAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? OpenAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? DeleteAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? CopyAgentMessageRequested;

    public event EventHandler<RoutedEventArgs>? ForkAgentConversationRequested;

    public event EventHandler<RoutedEventArgs>? SelectAgentModelRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentModelFavoriteRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentModelsRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? SendAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? QueueAgentFollowUpRequested;

    public event EventHandler<RoutedEventArgs>? AttachAgentImageRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentImagesRequested;

    public event EventHandler<RoutedEventArgs>? ShowAgentSettingsRequested;

    public event EventHandler<RoutedEventArgs>? SubmitAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentPinRequested;

    private static void OnAgentChatTranscriptScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer transcript || e.ExtentDelta.Y <= 0)
        {
            return;
        }

        var previousExtentHeight = transcript.Extent.Height - e.ExtentDelta.Y;
        var previousEndOffset = Math.Max(
            0,
            previousExtentHeight - transcript.Viewport.Height);
        if (transcript.Offset.Y < previousEndOffset - 12)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(transcript.ScrollToEnd);
    }

    private void OnAgentQuestionResponseKeyDown(object? sender, KeyEventArgs e) =>
        AgentQuestionResponseKeyDownRequested?.Invoke(sender, e);

    private void OnAgentPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ShouldSubmitPrompt(e.Key, e.KeyModifiers))
        {
            return;
        }

        e.Handled = true;
        SendAgentChatRequested?.Invoke(sender, e);
    }

    internal static bool ShouldSubmitPrompt(Key key, KeyModifiers modifiers) =>
        key == Key.Enter && modifiers == KeyModifiers.None;

    private void OnApproveAgentActionClick(object? sender, RoutedEventArgs e) =>
        ApproveAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentActionClick(object? sender, RoutedEventArgs e) =>
        CancelAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentChatClick(object? sender, RoutedEventArgs e) =>
        CancelAgentChatRequested?.Invoke(sender, e);

    private void OnClearAgentChatClick(object? sender, RoutedEventArgs e) =>
        ClearAgentChatRequested?.Invoke(sender, e);

    private void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        DeclineAgentQuestionRequested?.Invoke(sender, e);

    private void OnDenyAgentActionClick(object? sender, RoutedEventArgs e) =>
        DenyAgentActionRequested?.Invoke(sender, e);

    private void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        DisableAgentYoloRequested?.Invoke(sender, e);

    private void OnEnableAgentCapabilityAskClick(object? sender, RoutedEventArgs e) =>
        EnableAgentCapabilityAskRequested?.Invoke(sender, e);

    private void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        EnableAgentYoloRequested?.Invoke(sender, e);

    private void OnKeepAgentCapabilityOffClick(object? sender, RoutedEventArgs e) =>
        KeepAgentCapabilityOffRequested?.Invoke(sender, e);

    private void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e) =>
        LoadOlderAgentAuditRequested?.Invoke(sender, e);

    private void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentAuditRequested?.Invoke(sender, e);

    private void OnSendAgentChatClick(object? sender, RoutedEventArgs e) =>
        SendAgentChatRequested?.Invoke(sender, e);

    private void OnQueueAgentFollowUpClick(object? sender, RoutedEventArgs e) =>
        QueueAgentFollowUpRequested?.Invoke(sender, e);

    private void OnAttachAgentImageClick(object? sender, RoutedEventArgs e) =>
        AttachAgentImageRequested?.Invoke(sender, e);

    private void OnClearAgentImagesClick(object? sender, RoutedEventArgs e) =>
        ClearAgentImagesRequested?.Invoke(sender, e);

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowAgentSettingsRequested?.Invoke(sender, e);

    private void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        SubmitAgentQuestionRequested?.Invoke(sender, e);

    private void OnToggleAgentPinClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentPinRequested?.Invoke(sender, e);

    private void OnStartNewConversationClick(object? sender, RoutedEventArgs e) =>
        StartNewAgentConversationRequested?.Invoke(sender, e);

    private void OnOpenConversationClick(object? sender, RoutedEventArgs e) =>
        OpenAgentConversationRequested?.Invoke(sender, e);

    private void OnDeleteConversationClick(object? sender, RoutedEventArgs e) =>
        DeleteAgentConversationRequested?.Invoke(sender, e);

    private void OnCopyAgentMessageClick(object? sender, RoutedEventArgs e) =>
        CopyAgentMessageRequested?.Invoke(sender, e);

    private void OnForkAgentConversationClick(object? sender, RoutedEventArgs e) =>
        ForkAgentConversationRequested?.Invoke(sender, e);

    private void OnSelectModelClick(object? sender, RoutedEventArgs e) =>
        SelectAgentModelRequested?.Invoke(sender, e);

    private void OnToggleFavoriteModelClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentModelFavoriteRequested?.Invoke(sender, e);

    private void OnRefreshModelsClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentModelsRequested?.Invoke(sender, e);
}
