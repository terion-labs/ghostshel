using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GhostShell.App.Views;
using GhostShell.Application;
using GhostShell.Core;
using ImageMagick;
using ImageMagick.Drawing;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace GhostShell.DesignQa;

/// <summary>
/// Turns deterministic QA routes into website-ready window assets. The app
/// supplies the pixels inside the frame; this boundary supplies only chrome
/// that the headless platform cannot draw and the alpha mask a browser needs.
/// </summary>
internal static class WebsiteScreenshotExport
{
    public const int Width = 1440;
    public const int Height = 900;

    private const double TrafficLightDiameter = 12;
    private const double TrafficLightTop = 16;
    private const double TrafficLightFirstLeft = 18;
    private const double TrafficLightSpacing = 20;
    private const int DialogInset = 72;

    private static readonly double CornerRadius =
        DensityCornerScale.WindowRadius(InterfaceDensity.Cozy);

    public static ThemePreference NormalizeTheme(ThemePreference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ThemePreference(
            source.Id,
            source.Name,
            source.Appearance,
            source.PlatformProfile,
            AccentPreference.GhostShellBronze,
            source.TextScaleOverride,
            source.Density,
            showTabBar: true,
            source.ShowWorkspacesPanel,
            TabStripPlacement.Top,
            source.WorkspacePanelPlacement,
            isTranslucent: true,
            source.BackdropOpacityPercent,
            source.HasGlassPanels,
            overridesBackdropOpacity: true);
    }

    public static DefinitionCatalogSnapshot NormalizeSnapshot(
        DefinitionCatalogSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source with
        {
            Themes =
            [
                .. source.Themes.Select(stored =>
                    stored with { Value = NormalizeTheme(stored.Value) }),
            ],
        };
    }

    public static void PrepareWindow(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Background = Brushes.Transparent;
        window.Width = Width;
        window.Height = Height;

        if (window.Content is not Panel shell)
        {
            throw new InvalidOperationException(
                "Website export requires the MainWindow panel root.");
        }

        var trafficLights = new Canvas
        {
            Width = 82,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        trafficLights.Children.Add(TrafficLight("#FF5F57", 0));
        trafficLights.Children.Add(TrafficLight("#FEBC2E", 1));
        trafficLights.Children.Add(TrafficLight("#28C840", 2));
        trafficLights.SetValue(Panel.ZIndexProperty, int.MaxValue);
        shell.Children.Add(trafficLights);
    }

    public static void FinishFrame(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var image = new MagickImage(path);
        ApplyWindowShape(image);
        image.Format = MagickFormat.Png;
        image.Write(path);
    }

    public static void WriteDialogFrame(
        MainWindow backdrop,
        Control dialog,
        PixelSize dialogSize,
        Vector dialogDpi,
        string path)
    {
        ArgumentNullException.ThrowIfNull(backdrop);
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var backdropBytes = Render(
            backdrop,
            new PixelSize(Width, Height),
            new Vector(96, 96));
        using var dialogBytes = Render(dialog, dialogSize, dialogDpi);
        using var frame = new MagickImage(backdropBytes);
        using var dialogImage = new MagickImage(dialogBytes);

        FitDialog(dialogImage);
        var x = ((int)frame.Width - (int)dialogImage.Width) / 2;
        var y = ((int)frame.Height - (int)dialogImage.Height) / 2;
        frame.Composite(dialogImage, x, y, CompositeOperator.Over);
        ApplyWindowShape(frame);
        frame.Format = MagickFormat.Png;
        frame.Write(path);
    }

    public static void WriteChromeMask(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var mask = CreateWindowMask();
        mask.Format = MagickFormat.Png;
        var path = Path.Combine(outputDirectory, "window-chrome-mask.png");
        mask.Write(path);
        Console.WriteLine($"MASK window chrome -> {path} ({Width}x{Height})");
    }

    private static Ellipse TrafficLight(string color, int index)
    {
        var light = new Ellipse
        {
            Width = TrafficLightDiameter,
            Height = TrafficLightDiameter,
            Fill = new SolidColorBrush(Color.Parse(color)),
            Stroke = new SolidColorBrush(Color.Parse("#33000000")),
            StrokeThickness = 0.75,
        };
        Canvas.SetLeft(light, TrafficLightFirstLeft + (TrafficLightSpacing * index));
        Canvas.SetTop(light, TrafficLightTop);
        return light;
    }

    private static MemoryStream Render(Control target, PixelSize size, Vector dpi)
    {
        var bytes = new MemoryStream();
        using var bitmap = new RenderTargetBitmap(size, dpi);
        bitmap.Render(target);
        bitmap.Save(bytes);
        bytes.Position = 0;
        return bytes;
    }

    private static void FitDialog(MagickImage dialog)
    {
        var maximumWidth = (uint)(Width - (DialogInset * 2));
        var maximumHeight = (uint)(Height - (DialogInset * 2));
        if (dialog.Width <= maximumWidth && dialog.Height <= maximumHeight)
        {
            return;
        }

        dialog.Resize(new MagickGeometry(maximumWidth, maximumHeight)
        {
            IgnoreAspectRatio = false,
            Greater = true,
        });
    }

    private static void ApplyWindowShape(MagickImage image)
    {
        if (image.Width != Width || image.Height != Height)
        {
            throw new InvalidOperationException(
                $"Website frames must be {Width}x{Height}; got {image.Width}x{image.Height}.");
        }

        using var mask = CreateWindowMask();
        image.Alpha(AlphaOption.Set);
        image.Composite(mask, CompositeOperator.DstIn);
    }

    private static MagickImage CreateWindowMask()
    {
        var mask = new MagickImage(MagickColors.Transparent, Width, Height);
        new Drawables()
            .FillColor(MagickColors.White)
            .StrokeColor(MagickColors.Transparent)
            .RoundRectangle(0, 0, Width - 1, Height - 1, CornerRadius, CornerRadius)
            .Draw(mask);
        return mask;
    }
}
