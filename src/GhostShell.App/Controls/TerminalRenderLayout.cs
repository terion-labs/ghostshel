using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Controls;

internal readonly record struct TerminalCellMetrics(
    double CellWidth,
    double CellHeight,
    Thickness Padding,
    int Columns,
    int Rows)
{
    private static readonly ConcurrentDictionary<FontMetricKey, (double Width, double Height)> FontMetrics = new();

    public static TerminalCellMetrics Measure(
        Size availableSize,
        TerminalRenderProfileSnapshot? profile)
    {
        var fontSize = profile?.FontSize ?? 13;
        var lineHeight = profile?.LineHeight ?? 1;
        var fontFamily = profile?.FontFamily
            ?? "JetBrains Mono, Menlo, Consolas, monospace";
        var padding = new Thickness(12, 10);
        var metricKey = new FontMetricKey(fontFamily, fontSize);
        var measured = FontMetrics.TryGetValue(metricKey, out var cached)
            ? cached
            : MeasureFont(metricKey);
        var cellWidth = Math.Max(1, measured.Width);
        var cellHeight = Math.Max(1, measured.Height * lineHeight);
        var contentWidth = Math.Max(0, availableSize.Width - padding.Left - padding.Right);
        var contentHeight = Math.Max(0, availableSize.Height - padding.Top - padding.Bottom);
        var columns = Math.Clamp((int)Math.Floor(contentWidth / cellWidth), 2, 1_000);
        var rows = Math.Clamp((int)Math.Floor(contentHeight / cellHeight), 1, 1_000);
        return new TerminalCellMetrics(cellWidth, cellHeight, padding, columns, rows);
    }

    public ViewportDescriptor ToViewport(Size availableSize, double renderScale) => new(
        Math.Max(0, availableSize.Width - Padding.Left - Padding.Right),
        Math.Max(0, availableSize.Height - Padding.Top - Padding.Bottom),
        Math.Max(0.1, renderScale),
        Columns,
        Rows);

    public Rect CellBounds(int row, int column, int width = 1) => new(
        Padding.Left + column * CellWidth,
        Padding.Top + row * CellHeight,
        Math.Max(1, width) * CellWidth,
        CellHeight);

    public (int Column, int Row) CellAt(Point point) => (
        Math.Clamp((int)Math.Floor((point.X - Padding.Left) / CellWidth), 0, Columns - 1),
        Math.Clamp((int)Math.Floor((point.Y - Padding.Top) / CellHeight), 0, Rows - 1));

    private readonly record struct FontMetricKey(string FontFamily, double FontSize);

    private static (double Width, double Height) MeasureFont(FontMetricKey key)
    {
        try
        {
            var glyph = new FormattedText(
                "M",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(key.FontFamily),
                key.FontSize,
                Brushes.White);
            var measured = (glyph.Width, glyph.Height);
            FontMetrics.TryAdd(key, measured);
            return measured;
        }
        catch (InvalidOperationException)
        {
            // Design-time and plain unit-test processes do not install Avalonia's
            // platform font manager. The desktop process always takes the shaped path.
            return (key.FontSize * 0.62, key.FontSize * 1.2);
        }
    }
}

internal sealed record TerminalRenderCell(
    int Row,
    int Column,
    int Width,
    string Text,
    Color Foreground,
    Color Background,
    TerminalCellStyle Style,
    string? Hyperlink,
    bool IsSelected);

internal sealed record TerminalRenderFrame(
    IReadOnlyList<TerminalRenderCell> Cells,
    int CursorRow,
    int CursorColumn,
    bool IsCursorVisible);

internal static class TerminalRenderLayout
{
    public static TerminalRenderFrame Create(
        TerminalScreenSnapshot snapshot,
        TerminalRenderProfileSnapshot? profile,
        TerminalCellMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var palette = profile?.Palette ?? TerminalPalette.GhostShellDark;
        var cells = new List<TerminalRenderCell>(
            Math.Min(metrics.Rows * metrics.Columns, 32_768));

        foreach (var row in snapshot.StructuredRows)
        {
            if (row.Index >= metrics.Rows)
            {
                break;
            }

            var column = 0;
            foreach (var cell in row.Cells)
            {
                if (cell.Width == 0)
                {
                    continue;
                }

                if (column >= metrics.Columns)
                {
                    break;
                }

                var width = Math.Min(cell.Width, metrics.Columns - column);
                var colors = TerminalCellColors.Resolve(cell, palette);
                cells.Add(new TerminalRenderCell(
                    row.Index,
                    column,
                    width,
                    cell.Text,
                    colors.Foreground,
                    colors.Background,
                    cell.Style,
                    cell.Hyperlink,
                    cell.IsSelected));
                column += cell.Width;
            }
        }

        return new TerminalRenderFrame(
            cells,
            Math.Clamp(snapshot.CursorRow, 0, metrics.Rows - 1),
            Math.Clamp(snapshot.CursorColumn, 0, metrics.Columns - 1),
            snapshot.IsCursorVisible);
    }
}

internal readonly record struct TerminalResolvedColors(Color Foreground, Color Background);

internal static class TerminalCellColors
{
    public static TerminalResolvedColors Resolve(
        TerminalScreenCell cell,
        TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(palette);
        var foreground = Resolve(cell.Foreground, palette, foreground: true);
        var background = Resolve(cell.Background, palette, foreground: false);
        if (cell.Style.HasFlag(TerminalCellStyle.Inverse))
        {
            (foreground, background) = (background, foreground);
        }

        if (cell.Style.HasFlag(TerminalCellStyle.Dim))
        {
            foreground = Color.FromArgb(166, foreground.R, foreground.G, foreground.B);
        }

        if (cell.Style.HasFlag(TerminalCellStyle.Invisible))
        {
            foreground = background;
        }

        if (cell.IsSelected)
        {
            foreground = FromRgb(palette.Foreground);
            background = FromRgb(palette.SelectionBackground);
        }

        return new TerminalResolvedColors(foreground, background);
    }

    public static Color Resolve(
        TerminalCellColor color,
        TerminalPalette palette,
        bool foreground)
    {
        ArgumentNullException.ThrowIfNull(color);
        ArgumentNullException.ThrowIfNull(palette);
        return color.Mode switch
        {
            TerminalColorMode.Default => FromRgb(
                foreground ? palette.Foreground : palette.Background),
            TerminalColorMode.Rgb => Color.FromRgb(
                (byte)((color.Value >> 16) & 0xFF),
                (byte)((color.Value >> 8) & 0xFF),
                (byte)(color.Value & 0xFF)),
            TerminalColorMode.Indexed => Indexed(color.Value, palette),
            _ => throw new ArgumentOutOfRangeException(
                nameof(color),
                color.Mode,
                "Unknown terminal color mode."),
        };
    }

    private static Color Indexed(int index, TerminalPalette palette)
    {
        if (index < 16)
        {
            return FromRgb(palette.AnsiColors[index]);
        }

        if (index < 232)
        {
            var cube = index - 16;
            var red = cube / 36;
            var green = cube / 6 % 6;
            var blue = cube % 6;
            return Color.FromRgb(Cube(red), Cube(green), Cube(blue));
        }

        var gray = (byte)(8 + (index - 232) * 10);
        return Color.FromRgb(gray, gray, gray);
    }

    private static byte Cube(int component) => component == 0
        ? (byte)0
        : (byte)(55 + component * 40);

    private static Color FromRgb(RgbColor color) =>
        Color.FromRgb(color.Red, color.Green, color.Blue);
}
