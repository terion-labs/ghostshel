using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using CSharpMath.Avalonia;

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

    public static readonly StyledProperty<bool> ContinuousSelectionProperty =
        AvaloniaProperty.Register<MarkdownPreviewView, bool>(nameof(ContinuousSelection));

    /// <summary>
    /// Heading sizes, largest first. Markdown allows six levels; the shell's
    /// type scale is what decides how big each one is here.
    /// </summary>
    private static readonly double[] HeadingSizes = [23, 19, 16, 14, 13, 13];

    /// <summary>Body size for prose, a step above the shell's dense UI text.</summary>
    private const double BodyFontSize = 13;

    public MarkdownPreviewView()
    {
        InitializeComponent();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Renders the document as one selectable text surface. Chat messages use
    /// this because selection belongs to the message, not to Markdown's
    /// internal paragraph and list-item boundaries.
    /// </summary>
    public bool ContinuousSelection
    {
        get => GetValue(ContinuousSelectionProperty);
        set => SetValue(ContinuousSelectionProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Resources resolve against the tree, so a render before attachment
        // would silently draw every themed brush as nothing.
        _hasRendered = false;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _building?.Cancel();
        _building?.Dispose();
        _building = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == TextProperty || change.Property == ContinuousSelectionProperty)
            && VisualRoot is not null)
        {
            if (change.Property == ContinuousSelectionProperty)
            {
                _hasRendered = false;
            }

            Render();
        }
    }

    /// <summary>The text the blocks on screen were built from.</summary>
    private string? _rendered;

    private bool _hasRendered;

    private void Render()
    {
        // Rebuilding costs a syntax-highlighting installation per fenced block,
        // so the same text is never laid out twice. Switching a preview to its
        // source and back hands us the identical string.
        if (_hasRendered && string.Equals(_rendered, Text, StringComparison.Ordinal))
        {
            return;
        }

        _rendered = Text;
        _hasRendered = true;
        _building?.Cancel();
        _building?.Dispose();
        _building = new CancellationTokenSource();
        _ = BuildAsync(Text, _building.Token);
    }

    /// <summary>
    /// Blocks arrive a few at a time. A document is laid out on the thread that
    /// draws — there is nowhere else to build controls — so the work is cut
    /// into steps and the thread handed back between them; a document twice as
    /// long then takes twice as many steps rather than one twice as long.
    /// </summary>
    private async Task BuildAsync(string? markdown, CancellationToken token)
    {
        try
        {
            // Provider streams can update a partial Markdown block many times
            // in one frame. Parse only the latest value, away from Avalonia's
            // UI thread, then build native controls in short UI-thread steps.
            await Task.Delay(TimeSpan.FromMilliseconds(24), token);
            var blocks = await Task.Run(
                () => MarkdownPreviewDocument.Parse(markdown),
                token);
            token.ThrowIfCancellationRequested();
            Blocks.Children.Clear();
            if (ContinuousSelection)
            {
                if (!blocks.IsEmpty)
                {
                    Blocks.Children.Add(ContinuousDocument(blocks));
                }

                return;
            }

            for (var index = 0; index < blocks.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (Build(blocks[index]) is { } control)
                {
                    Blocks.Children.Add(control);
                }

                if ((index + 1) % BlocksPerStep == 0 && index + 1 < blocks.Length)
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Blocks laid out before the thread is handed back. A fenced block is the
    /// expensive one, so the step is small.
    /// </summary>
    private const int BlocksPerStep = 8;

    private CancellationTokenSource? _building;

    /// <summary>
    /// Avalonia selection cannot cross control boundaries. Keeping all prose
    /// in one native document gives it one drag-selection range while its
    /// block layout retains headings and hanging list indents. Markdown syntax
    /// markers are not exposed in the text being selected.
    /// </summary>
    private Control ContinuousDocument(ImmutableArray<MarkdownBlock> blocks)
    {
        if (!blocks.Any(IsEmbeddedBlock))
        {
            return ContinuousText(blocks);
        }

        // Fenced code and diagrams are real controls, so they cannot live
        // inside the prose document. Keep every contiguous prose region as one
        // selection surface and place embedded blocks at their Markdown
        // position. This is the same code/diagram renderer file previews use.
        var document = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = Metric("ShellSpaceSm", 8),
        };
        var prose = ImmutableArray.CreateBuilder<MarkdownBlock>();
        foreach (var block in blocks)
        {
            if (!IsEmbeddedBlock(block))
            {
                prose.Add(block);
                continue;
            }

            AddContinuousText(document, prose);
            document.Children.Add(IsMermaid(block) ? Mermaid(block) : Code(block));
        }

        AddContinuousText(document, prose);
        return document;
    }

    private void AddContinuousText(
        Panel document,
        ImmutableArray<MarkdownBlock>.Builder prose)
    {
        if (prose.Count == 0)
        {
            return;
        }

        document.Children.Add(ContinuousText(prose.ToImmutable()));
        prose.Clear();
    }

    private static Control ContinuousText(ImmutableArray<MarkdownBlock> blocks) =>
        new SelectableMarkdownDocument(blocks);

    private Control? Build(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => Heading(block),
        MarkdownBlockKind.Paragraph => Paragraph(block),
        MarkdownBlockKind.ListItem => ListItem(block),
        MarkdownBlockKind.Quote => Quote(block),
        MarkdownBlockKind.Code => IsMermaid(block) ? Mermaid(block) : Code(block),
        MarkdownBlockKind.ThematicBreak => ThematicBreak(),
        MarkdownBlockKind.Table => Table(block),
        MarkdownBlockKind.Math => Formula(block),
        _ => null,
    };

    private static bool IsMermaid(MarkdownBlock block) =>
        block.Kind == MarkdownBlockKind.Code
        && string.Equals(block.Language?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmbeddedBlock(MarkdownBlock block) =>
        block.Kind == MarkdownBlockKind.Code;

    private Control Mermaid(MarkdownBlock block)
    {
        var diagram = new DatabaseMermaidDiagramView
        {
            MermaidSource = block.Text ?? string.Empty,
            Height = 320,
            MinHeight = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(diagram, "Rendered Mermaid diagram");
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = diagram,
            Background = Brush("ShellBackgroundBrush") ?? Brushes.Transparent,
            BorderBrush = Brush("ShellBorderBrush") ?? Brushes.Gray
        };
        return frame;
    }

    private Control Heading(MarkdownBlock block)
    {
        var text = Prose(block.Runs);
        var size = HeadingSizes[Math.Clamp(block.Level, 1, HeadingSizes.Length) - 1];
        text.FontSize = size;
        text.LineHeight = Math.Round(size * 1.3);
        text.FontWeight = FontWeight.SemiBold;
        // Space above a heading, not below: a heading belongs to what follows
        // it, and an even gap on both sides makes it float between sections.
        text.Margin = new Thickness(0, block.Level == 1 ? 2 : 14, 0, 2);
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
            Margin = new Thickness((Math.Max(0, block.Level - 1) * 18) + 2, 0, 0, 0),
        };
        var bullet = new SelectableTextBlock
        {
            Text = string.IsNullOrEmpty(block.Bullet) ? string.Empty : block.Bullet,
            MinWidth = 20,
            // The same size and line height as the text beside it, or the
            // marker rides above the first line instead of sitting on it.
            FontSize = BodyFontSize,
            LineHeight = Math.Round(BodyFontSize * 1.55),
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
        content.Margin = new Thickness(12, 4, 0, 4);
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
        var editor = new CodePreviewView
        {
            Text = block.Text,
            FileName = block.Language is null ? null : $"fenced.{block.Language}",
            // Sized by the editor's own line height: a guessed height clips the
            // last line or leaves a screen of empty gutter under three lines.
            FitsContent = true,
        };
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(6, 4),
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

    private Control Formula(MarkdownBlock block)
    {
        var formula = new MathView
        {
            LaTeX = block.Text,
            DisplayErrorInline = false,
            FontSize = (float)BodyFontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4),
        };
        if (Brush("ShellTextBrush") is ISolidColorBrush foreground)
        {
            formula.TextColor = foreground.Color;
        }

        return formula.ErrorMessage is null
            ? formula
            : new SelectableTextBlock
            {
                Text = block.Text,
                FontSize = BodyFontSize,
                TextWrapping = TextWrapping.Wrap,
            };
    }

    private Control ThematicBreak()
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 6),
            Background = Brush("ShellBorderBrush") ?? Brushes.Gray
        };
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
            Margin = new Thickness(0, 4, 0, 4),
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
            FontSize = BodyFontSize,
            // Prose at a terminal's line spacing reads as a wall; 1.55 is the
            // ratio the shell's own documentation surfaces use.
            LineHeight = Math.Round(BodyFontSize * 1.55),
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var run in runs)
        {
            text.Inlines?.Add(Inline(run));
        }

        return text;
    }

    private Inline Inline(MarkdownRun run)
    {
        if (run.Style.HasFlag(MarkdownRunStyle.Math))
        {
            var formula = new MathView
            {
                LaTeX = run.Text,
                DisplayErrorInline = false,
                FontSize = (float)BodyFontSize,
            };
            if (Brush("ShellTextBrush") is ISolidColorBrush foreground)
            {
                formula.TextColor = foreground.Color;
            }

            if (formula.ErrorMessage is null)
            {
                return new InlineUIContainer { Child = formula };
            }
        }

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

    private double Metric(string key, double fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is double metric
            ? metric
            : fallback;

    private T? Resource<T>(string key)
        where T : class =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as T : null;
}
