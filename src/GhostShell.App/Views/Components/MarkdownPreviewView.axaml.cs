using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace GhostShell.App.Views.Components;

/// <summary>
/// Renders Markdown as native controls rather than as a web page: the text is
/// real text, so it selects, copies, and scales with the shell's own type and
/// spacing instead of a browser's.
/// </summary>
public sealed partial class MarkdownPreviewView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownPreviewView, string?>(nameof(Text));

    /// <summary>
    /// Heading sizes, largest first. Markdown allows six levels; the shell's
    /// type scale is what decides how big each one is here.
    /// </summary>
    private static readonly double[] HeadingSizes = [22, 18, 16, 14, 13, 12];

    public MarkdownPreviewView()
    {
        InitializeComponent();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Resources resolve against the tree, so a render before attachment
        // would silently draw every themed brush as nothing.
        Render();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Render();
        }
    }

    private void Render()
    {
        Blocks.Children.Clear();
        foreach (var block in MarkdownPreviewDocument.Parse(Text))
        {
            var control = Build(block);
            if (control is not null)
            {
                Blocks.Children.Add(control);
            }
        }
    }

    private Control? Build(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => Heading(block),
        MarkdownBlockKind.Paragraph => Paragraph(block),
        MarkdownBlockKind.ListItem => ListItem(block),
        MarkdownBlockKind.Quote => Quote(block),
        MarkdownBlockKind.Code => Code(block),
        MarkdownBlockKind.ThematicBreak => ThematicBreak(),
        MarkdownBlockKind.Table => Table(block),
        _ => null,
    };

    private Control Heading(MarkdownBlock block)
    {
        var text = Prose(block.Runs);
        text.FontSize = HeadingSizes[Math.Clamp(block.Level, 1, HeadingSizes.Length) - 1];
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, block.Level == 1 ? 0 : 8, 0, 0);
        return text;
    }

    private Control Paragraph(MarkdownBlock block) => Prose(block.Runs);

    private Control ListItem(MarkdownBlock block)
    {
        // The bullet is its own column so wrapped lines line up under the text
        // rather than under the marker.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(Math.Max(0, block.Level - 1) * 16, 0, 0, 0),
        };
        var bullet = new SelectableTextBlock
        {
            Text = string.IsNullOrEmpty(block.Bullet) ? string.Empty : block.Bullet + " ",
            MinWidth = 18,
        };
        Paint(bullet, "ShellMutedBrush");
        var content = Prose(block.Runs);
        Grid.SetColumn(content, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(content);
        return grid;
    }

    private Control Quote(MarkdownBlock block)
    {
        var content = Prose(block.Runs);
        Paint(content, "ShellMutedBrush");
        content.Margin = new Thickness(10, 2, 0, 2);
        var quote = new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            Child = content,
        };
        if (Brush("ShellAccentBrush") is { } accent)
        {
            quote.BorderBrush = accent;
        }

        return quote;
    }

    private Control Code(MarkdownBlock block)
    {
        // The same source view the file preview uses, so a fenced block is
        // highlighted exactly like the file it was copied from.
        var lines = 1 + (block.Text?.Count(character => character == '\n') ?? 0);
        var editor = new CodePreviewView
        {
            Text = block.Text,
            FileName = block.Language is null ? null : $"fenced.{block.Language}",
            // Sized to the code it holds: an editor left to fill its allowance
            // leaves a screen of empty gutter under three lines of code.
            Height = Math.Min(320, (lines * 17) + 12),
        };
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = editor,
        };
        if (Brush("ShellBackgroundBrush") is { } fill)
        {
            frame.Background = fill;
        }

        if (Brush("ShellBorderBrush") is { } edge)
        {
            frame.BorderBrush = edge;
        }

        return frame;
    }

    private Control ThematicBreak()
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 6),
        };
        rule.Background = Brush("ShellBorderBrush") ?? Brushes.Gray;
        return rule;
    }

    private Control Table(MarkdownBlock block)
    {
        var columns = Math.Max(
            block.HeaderCells.Length,
            block.Rows.Length == 0 ? 0 : block.Rows.Max(row => row.Length));
        if (columns == 0)
        {
            return new Border();
        }

        var grid = new Grid();
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var row = 0;
        if (!block.HeaderCells.IsEmpty)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < block.HeaderCells.Length; column++)
            {
                var cell = Cell(block.HeaderCells[column], isHeader: true);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }

            row++;
        }

        foreach (var cells in block.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < cells.Length; column++)
            {
                var cell = Cell(cells[column], isHeader: false);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }

            row++;
        }

        var table = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
        if (Brush("ShellBorderBrush") is { } outline)
        {
            table.BorderBrush = outline;
        }

        return table;
    }

    private Control Cell(ImmutableArray<MarkdownRun> runs, bool isHeader)
    {
        var content = Prose(runs);
        content.Margin = new Thickness(10, 5);
        if (isHeader)
        {
            content.FontWeight = FontWeight.SemiBold;
        }

        var cell = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = content,
        };
        if (Brush("ShellBorderBrush") is { } grid)
        {
            cell.BorderBrush = grid;
        }

        return cell;
    }

    /// <summary>
    /// One selectable block of text carrying every run's appearance as inlines,
    /// so a whole paragraph selects and copies as one piece of prose.
    /// </summary>
    private SelectableTextBlock Prose(ImmutableArray<MarkdownRun> runs)
    {
        var text = new SelectableTextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var run in runs)
        {
            text.Inlines?.Add(Inline(run));
        }

        return text;
    }

    private Run Inline(MarkdownRun run)
    {
        var inline = new Run(run.Text);
        if (run.Style.HasFlag(MarkdownRunStyle.Bold))
        {
            inline.FontWeight = FontWeight.SemiBold;
        }

        if (run.Style.HasFlag(MarkdownRunStyle.Italic))
        {
            inline.FontStyle = FontStyle.Italic;
        }

        if (run.Style.HasFlag(MarkdownRunStyle.Strikethrough))
        {
            inline.TextDecorations = TextDecorations.Strikethrough;
        }

        if (run.Style.HasFlag(MarkdownRunStyle.Code))
        {
            if (Resource<FontFamily>("ShellDataFontFamily") is { } mono)
            {
                inline.FontFamily = mono;
            }

            Tint(inline, "ShellAccentBrush");
        }

        if (run.LinkTarget is not null)
        {
            // Shown as a link and copyable as text; a preview does not open
            // things on the user's behalf.
            Tint(inline, "ShellAccentBrush");
            inline.TextDecorations = TextDecorations.Underline;
        }

        return inline;
    }

    /// <summary>
    /// Applies a themed foreground only when the theme actually has one:
    /// assigning a missing resource would paint the text with nothing, which
    /// draws as nothing.
    /// </summary>
    private void Paint(TextBlock text, string key)
    {
        if (Brush(key) is { } brush)
        {
            text.Foreground = brush;
        }
    }

    private void Tint(Run inline, string key)
    {
        if (Brush(key) is { } brush)
        {
            inline.Foreground = brush;
        }
    }

    private IBrush? Brush(string key) => Resource<IBrush>(key);

    private T? Resource<T>(string key)
        where T : class =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as T : null;
}
