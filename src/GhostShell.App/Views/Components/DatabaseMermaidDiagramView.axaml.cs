using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Avalonia.VisualTree;
using Mermaider;
using DiagramTypes = Mermaider.Models.DiagramTypes;
using RenderOptions = Mermaider.Models.RenderOptions;
using SanitizeMode = Mermaider.Models.SanitizeMode;

namespace GhostShell.App.Views.Components;

/// <summary>
/// Renders the database's Mermaid source as a native, themed SVG and provides
/// the small set of navigation gestures a large schema needs.
/// </summary>
public sealed partial class DatabaseMermaidDiagramView : UserControl
{
    public static readonly StyledProperty<string> MermaidSourceProperty =
        AvaloniaProperty.Register<DatabaseMermaidDiagramView, string>(
            nameof(MermaidSource),
            string.Empty);

    private const double MinimumZoom = 0.5;
    private const double MaximumZoom = 8;
    private const double ZoomStep = 1.25;

    private Point _dragOrigin;
    private Vector _dragPan;
    private bool _dragging;
    private SvgSource? _svgSource;
    private CancellationTokenSource? _renderCancellation;
    private long _renderGeneration;

    public DatabaseMermaidDiagramView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => RequestRender();
        Viewport.PointerWheelChanged += OnPointerWheelChanged;
        Viewport.PointerPressed += OnPointerPressed;
        Viewport.PointerMoved += OnPointerMoved;
        Viewport.PointerReleased += OnPointerReleased;
        Viewport.DoubleTapped += (_, _) => FitDiagram();
    }

    public string MermaidSource
    {
        get => GetValue(MermaidSourceProperty);
        set => SetValue(MermaidSourceProperty, value);
    }

    /// <summary>The sanitized SVG currently presented by the vector control.</summary>
    public string RenderedSvg { get; private set; } = string.Empty;

    public bool HasRenderedDiagram => RenderedSvg.Length > 0
        && _svgSource?.Picture is not null
        && !ErrorCard.IsVisible;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MermaidSourceProperty)
        {
            RequestRender();
        }
    }

    private void RequestRender()
    {
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        var cancellationToken = _renderCancellation.Token;
        var generation = ++_renderGeneration;
        var source = MermaidSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            RenderedSvg = string.Empty;
            ReplaceSvgSource(null);
            ErrorCard.IsVisible = false;
            return;
        }

        var options = CreateRenderOptions();
        _ = RenderDiagramAsync(source, options, generation, cancellationToken);
    }

    private async Task RenderDiagramAsync(
        string source,
        RenderOptions options,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var renderedSvg = await Task.Run(
                () => MermaidRenderer.RenderSvg(source, options),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var svgSource = SvgSource.LoadFromSvg(renderedSvg);
            if (svgSource.Picture is null)
            {
                svgSource.Dispose();
                throw new InvalidOperationException("The rendered SVG contains no drawable picture.");
            }

            if (generation != _renderGeneration)
            {
                svgSource.Dispose();
                return;
            }

            RenderedSvg = renderedSvg;
            ReplaceSvgSource(svgSource);
            ErrorCard.IsVisible = false;
            FitDiagram();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer source/theme superseded this render.
        }
        catch (Exception exception)
        {
            if (generation != _renderGeneration)
            {
                return;
            }

            RenderedSvg = string.Empty;
            ReplaceSvgSource(null);
            ErrorText.Text = $"The ER diagram could not be rendered: {exception.Message}";
            ErrorCard.IsVisible = true;
        }
    }

    private RenderOptions CreateRenderOptions() => new()
    {
        AllowedDiagrams = DiagramTypes.Er,
        Bg = ThemeColor("ShellBackgroundBrush", "#111111"),
        Fg = ThemeColor("ShellTextBrush", "#FFFFFF"),
        Accent = ThemeColor("ShellAccentBrush", "#FF8400"),
        Muted = ThemeColor("ShellMutedBrush", "#B8B9B6"),
        Surface = ThemeColor("ShellSurfaceRaisedBrush", "#1A1A1A"),
        Border = ThemeColor("ShellBorderBrush", "#383838"),
        Line = ThemeColor("ShellMutedBrush", "#B8B9B6"),
        Font = "Inter",
        MonoFont = "JetBrains Mono",
        FontSize = "13px",
        Padding = 32,
        NodeSpacing = 36,
        LayerSpacing = 72,
        Transparent = false,
        SanitizeMode = SanitizeMode.Block,
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_svgSource is null && !string.IsNullOrWhiteSpace(MermaidSource))
        {
            RequestRender();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = null;
        ReplaceSvgSource(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void ReplaceSvgSource(SvgSource? source)
    {
        var previous = _svgSource;
        _svgSource = source;
        DiagramSurface.SvgSource = source;
        previous?.Dispose();
    }

    private string ThemeColor(string key, string fallback)
    {
        if (!this.TryFindResource(key, ActualThemeVariant, out var resource)
            || resource is not SolidColorBrush brush)
        {
            return fallback;
        }

        var color = brush.Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void OnZoomInClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ZoomBy(ZoomStep);

    private void OnZoomOutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ZoomBy(1 / ZoomStep);

    private void OnFitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        FitDiagram();

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!HasRenderedDiagram || Math.Abs(e.Delta.Y) < 0.01)
        {
            return;
        }

        ZoomBy(Math.Pow(ZoomStep, e.Delta.Y));
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!HasRenderedDiagram
            || !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _dragOrigin = e.GetPosition(Viewport);
        _dragPan = new Vector(DiagramSurface.PanX, DiagramSurface.PanY);
        Viewport.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(Viewport);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var delta = e.GetPosition(Viewport) - _dragOrigin;
        DiagramSurface.PanX = _dragPan.X + (delta.X / DiagramSurface.Zoom);
        DiagramSurface.PanY = _dragPan.Y + (delta.Y / DiagramSurface.Zoom);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Viewport.Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
    }

    private void ZoomBy(double factor)
    {
        DiagramSurface.Zoom = Math.Clamp(
            DiagramSurface.Zoom * factor,
            MinimumZoom,
            MaximumZoom);
        ZoomLevelText.Text = $"{Math.Round(DiagramSurface.Zoom * 100)}%";
    }

    private void FitDiagram()
    {
        DiagramSurface.Zoom = 1;
        DiagramSurface.PanX = 0;
        DiagramSurface.PanY = 0;
        ZoomLevelText.Text = "Fit";
    }
}
