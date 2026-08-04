using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace GhostShell.App.Controls;

/// <summary>
/// One row of a list: something on the left, a name and a line about it, and
/// whatever acts on it at the right.
///
/// Every list in the shell built this by hand — a three-column grid, a semibold
/// title, a muted detail under it — and no two agreed on the column spacing or
/// on the detail's size. The shape is the same everywhere it appears, so it is
/// a control and the differences between lists are what they put in it.
/// </summary>
internal sealed class ListItem : TemplatedControl
{
    public static readonly StyledProperty<object?> LeadingProperty =
        AvaloniaProperty.Register<ListItem, object?>(nameof(Leading));

    public static readonly StyledProperty<object?> TrailingProperty =
        AvaloniaProperty.Register<ListItem, object?>(nameof(Trailing));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ListItem, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<ListItem, string?>(nameof(Detail));

    static ListItem() =>
        DetailProperty.Changed.AddClassHandler<ListItem>((item, _) => item.UpdateStateClasses());

    public ListItem() => UpdateStateClasses();

    /// <summary>What identifies the row: a mark, an icon, a handle.</summary>
    public object? Leading
    {
        get => GetValue(LeadingProperty);
        set => SetValue(LeadingProperty, value);
    }

    /// <summary>What acts on it: a badge, a button, a check.</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// The line under the title. A row with nothing to add here keeps its title
    /// centred rather than sitting high over an empty line.
    /// </summary>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    private void UpdateStateClasses() =>
        PseudoClasses.Set(":detailed", !string.IsNullOrWhiteSpace(Detail));
}
