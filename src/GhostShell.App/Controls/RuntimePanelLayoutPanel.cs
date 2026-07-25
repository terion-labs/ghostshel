using Avalonia;
using Avalonia.Controls;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Controls;

/// <summary>
/// Arranges runtime panel presenters from the immutable durable layout copied
/// into each panel instance. Spans are preserved; no vendor-native type or
/// durable definition object crosses into the visual tree.
/// </summary>
public sealed class RuntimePanelLayoutPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var required = RequiredSize();
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(availableSize.Width, required.Width)
            : required.Width;
        var height = double.IsFinite(availableSize.Height)
            ? Math.Max(availableSize.Height, required.Height)
            : required.Height;
        var layoutSize = new Size(width, height);
        foreach (var child in Children)
        {
            child.Measure(LayoutBounds(child, layoutSize).Size);
        }

        return layoutSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var required = RequiredSize();
        var layoutSize = new Size(
            Math.Max(finalSize.Width, required.Width),
            Math.Max(finalSize.Height, required.Height));
        foreach (var child in Children)
        {
            child.Arrange(LayoutBounds(child, layoutSize));
        }

        return layoutSize;
    }

    private Size RequiredSize()
    {
        var panels = Children
            .Select(child => child.DataContext as RuntimePanelViewModel)
            .Where(panel => panel is not null)
            .Cast<RuntimePanelViewModel>()
            .ToArray();
        if (panels.Length == 0)
        {
            return default;
        }

        var columns = Math.Max(1, panels.Max(panel => panel.LayoutColumns));
        var rows = Math.Max(1, panels.Max(panel => panel.LayoutRows));
        var cellWidth = panels.Max(panel =>
            panel.LayoutMinimumWidth / Math.Max(1, panel.LayoutColumnSpan));
        var cellHeight = panels.Max(panel =>
            panel.LayoutMinimumHeight / Math.Max(1, panel.LayoutRowSpan));
        return new Size(columns * cellWidth, rows * cellHeight);
    }

    private static Rect LayoutBounds(Control child, Size size)
    {
        if (child.DataContext is not RuntimePanelViewModel panel)
        {
            return new Rect(size);
        }

        if (panel.IsZoomed)
        {
            return new Rect(size);
        }

        var columns = Math.Max(1, panel.LayoutColumns);
        var rows = Math.Max(1, panel.LayoutRows);
        var cellWidth = size.Width / columns;
        var cellHeight = size.Height / rows;
        return new Rect(
            panel.LayoutColumn * cellWidth,
            panel.LayoutRow * cellHeight,
            panel.LayoutColumnSpan * cellWidth,
            panel.LayoutRowSpan * cellHeight);
    }
}
