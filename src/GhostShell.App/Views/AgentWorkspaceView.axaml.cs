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

    public AgentWorkspaceView()
    {
        InitializeComponent();
        // A resize while floating is a local value over the style's size, so
        // docking clears it: the docked slot's geometry belongs to the layout
        // spacer, and the next float starts back at the style's default.
        Classes.CollectionChanged += (_, _) =>
        {
            if (!Classes.Contains("floating"))
            {
                ClearValue(WidthProperty);
                ClearValue(HeightProperty);
            }
        };
        FloatingResizeHandle.Cursor = DiagonalResizeCursor.Value;
    }

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
        var availableHeight = host.Bounds.Height - Margin.Top - Margin.Bottom;
        Width = Math.Clamp(
            Bounds.Width - e.Vector.X,
            MinimumFloatingWidth,
            Math.Max(MinimumFloatingWidth, availableWidth));
        Height = Math.Clamp(
            Bounds.Height + e.Vector.Y,
            MinimumFloatingHeight,
            Math.Max(MinimumFloatingHeight, availableHeight));
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

    public event EventHandler<RoutedEventArgs>? RefreshAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? SendAgentChatRequested;

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

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowAgentSettingsRequested?.Invoke(sender, e);

    private void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        SubmitAgentQuestionRequested?.Invoke(sender, e);

    private void OnToggleAgentPinClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentPinRequested?.Invoke(sender, e);
}
