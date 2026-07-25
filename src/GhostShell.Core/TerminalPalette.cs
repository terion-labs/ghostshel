using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// Terminal colors are deliberately owned by the terminal profile rather than the
/// application theme, so following a light host appearance never rewrites a pinned palette.
/// </summary>
public sealed record TerminalPalette
{
    [JsonConstructor]
    public TerminalPalette(
        string name,
        RgbColor foreground,
        RgbColor background,
        RgbColor cursor,
        RgbColor selectionBackground,
        IReadOnlyList<RgbColor> ansiColors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ansiColors);

        var colors = ansiColors.ToArray();
        if (colors.Length != 16)
        {
            throw new ArgumentException(
                "A terminal palette must define the standard and bright variants of all eight ANSI colors.",
                nameof(ansiColors));
        }

        Name = name;
        Foreground = foreground;
        Background = background;
        Cursor = cursor;
        SelectionBackground = selectionBackground;
        AnsiColors = Array.AsReadOnly(colors);
    }

    public string Name { get; }

    public RgbColor Foreground { get; }

    public RgbColor Background { get; }

    public RgbColor Cursor { get; }

    public RgbColor SelectionBackground { get; }

    public IReadOnlyList<RgbColor> AnsiColors { get; }

    public static TerminalPalette GhostShellDark { get; } = new(
        "GhostSHELL Dark",
        RgbColor.Parse("#E8E4DE"),
        RgbColor.Parse("#12100E"),
        RgbColor.Parse("#D9944D"),
        RgbColor.Parse("#4A3828"),
        [
            RgbColor.Parse("#1F1C19"),
            RgbColor.Parse("#D26060"),
            RgbColor.Parse("#72B57B"),
            RgbColor.Parse("#D1A85A"),
            RgbColor.Parse("#6B9BD2"),
            RgbColor.Parse("#B17AC5"),
            RgbColor.Parse("#66B8B2"),
            RgbColor.Parse("#D8D2C8"),
            RgbColor.Parse("#69625B"),
            RgbColor.Parse("#EE7B72"),
            RgbColor.Parse("#91D39A"),
            RgbColor.Parse("#EBC574"),
            RgbColor.Parse("#86B6EA"),
            RgbColor.Parse("#CD98DF"),
            RgbColor.Parse("#83D5CF"),
            RgbColor.Parse("#FFF9F0"),
        ]);
}
