using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public enum LayoutDesignerDirection
{
    Left,
    Right,
    Up,
    Down,
}

public enum LayoutDesignerEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

public enum LayoutDesignerCancelDisposition
{
    Close,
    ConfirmDiscard,
}

public sealed record LayoutDesignerOperationResult
{
    private LayoutDesignerOperationResult(
        bool isSuccess,
        DefinitionValidationIssue? issue)
    {
        IsSuccess = isSuccess;
        Issue = issue;
    }

    public static LayoutDesignerOperationResult Applied { get; } = new(true, null);

    public bool IsSuccess { get; }

    public DefinitionValidationIssue? Issue { get; }

    internal static LayoutDesignerOperationResult Rejected(DefinitionValidationIssue issue) =>
        new(false, issue ?? throw new ArgumentNullException(nameof(issue)));
}

public sealed record LayoutDesignerSaveRequest(
    LayoutDefinition Definition,
    long? ExpectedRevision);

/// <summary>
/// Immutable presentation snapshot for one layout slot. <see cref="Order"/> is also the
/// keyboard and accessibility traversal position.
/// </summary>
public sealed record LayoutDesignerSlotViewModel(
    int Order,
    LayoutSlotDefinition Definition,
    bool IsSelected,
    int Columns = 0,
    int Rows = 0)
{
    public LayoutSlotId Id => Definition.Id;

    public LayoutGridBounds Bounds => Definition.Bounds;

    public LayoutMinimumSize MinimumSize => Definition.MinimumSize;

    public string OrderLabel => $"Panel {Order}";

    public bool UsesOrangePalette => PaletteIndex == 0;

    public bool UsesBluePalette => PaletteIndex == 1;

    public bool UsesGreenPalette => PaletteIndex == 2;

    public bool UsesPinkPalette => PaletteIndex == 3;

    public string PositionLabel =>
        $"Column {Bounds.Column + 1}, row {Bounds.Row + 1}, "
        + $"{Bounds.ColumnSpan} by {Bounds.RowSpan}";

    /// <summary>
    /// The panel's size as a share of the grid — "½ × ¼" — which is what the
    /// panel will actually look like when the screen opens. Grid coordinates say
    /// where a panel is anchored; they do not say how big it looks, and reading
    /// "6 by 4" against a 12 × 8 grid is arithmetic the reader should not have to
    /// do. The exact coordinates stay available as the accessible name.
    /// </summary>
    public string SizeLabel => Columns > 0 && Rows > 0
        ? $"{Fraction(Bounds.ColumnSpan, Columns)} × {Fraction(Bounds.RowSpan, Rows)}"
        : string.Empty;

    /// <summary>
    /// Renders a span as a vulgar fraction where one exists, because "⅓" reads at
    /// a glance and "0.33" does not. Anything without a familiar glyph falls back
    /// to the plain ratio rather than an approximation.
    /// </summary>
    private static string Fraction(int span, int total)
    {
        if (span >= total)
        {
            return "1";
        }

        var divisor = GreatestCommonDivisor(span, total);
        var numerator = span / divisor;
        var denominator = total / divisor;
        return (numerator, denominator) switch
        {
            (1, 2) => "½",
            (1, 3) => "⅓",
            (2, 3) => "⅔",
            (1, 4) => "¼",
            (3, 4) => "¾",
            (1, 5) => "⅕",
            (1, 6) => "⅙",
            (1, 8) => "⅛",
            (3, 8) => "⅜",
            (5, 8) => "⅝",
            (7, 8) => "⅞",
            _ => $"{numerator}/{denominator}",
        };
    }

    private static int GreatestCommonDivisor(int first, int second)
    {
        while (second != 0)
        {
            (first, second) = (second, first % second);
        }

        return first;
    }

    private int PaletteIndex => (Order - 1) % 4;
}
