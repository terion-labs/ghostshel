using System.Text.RegularExpressions;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

/// <summary>
/// The rules that keep the interface one interface.
///
/// The shell drifted apart in a way nobody chose: 1,685 literal gaps, insets, and
/// radii across a hundred distinct values, eight kinds of card, and a destructive
/// button whose fixed width cut its own label off. None of it was a bad decision —
/// it was the absence of a place to put the decision, so each view answered again.
///
/// These tests are that place. The design system may not contain a literal at all,
/// and the rest of the markup may only ever contain fewer than it does today.
/// </summary>
public sealed partial class DesignSystemConventionTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    /// <summary>
    /// Layout properties that must resolve from a token, because each one is
    /// something the density, text-scale, corner-radius, or platform-profile
    /// setting is supposed to move.
    /// </summary>
    private static readonly string[] LayoutAxes =
    [
        "Margin",
        "Padding",
        "Spacing",
        "ColumnSpacing",
        "RowSpacing",
        "CornerRadius",
    ];

    /// <summary>
    /// How many literal layout values the application's markup may still contain.
    ///
    /// It is a ratchet, not a target. Eighteen hundred literals cannot be swept in
    /// one change without reviewing every view at once, which is how a refactor
    /// becomes the thing that broke the product. Lowering this number is the unit
    /// of progress; raising it needs a reason written down here.
    /// </summary>
    private const int LiteralLayoutBudget = 15;

    /// <summary>
    /// The design system defines what a value means, so it cannot contain one. A
    /// literal here is a setting that has silently stopped working for every view
    /// built on the component.
    /// </summary>
    [Fact]
    public void The_design_system_itself_contains_no_literal_layout_values()
    {
        foreach (var file in DesignSystemFiles())
        {
            var source = File.ReadAllText(file);
            var literals = LiteralLayoutValues(source).ToArray();

            Assert.True(
                literals.Length == 0,
                $"{Path.GetRelativePath(ApplicationViews.RepositoryRoot, file)} sets "
                + $"{string.Join(", ", literals)} to a literal. "
                + "Every value in the design system must come from a Shell* token.");
        }
    }

    /// <summary>
    /// Colour is the same argument as spacing: a hex in a component is a theme, a
    /// contrast guarantee, and a high-contrast mode that stop applying.
    /// </summary>
    [Fact]
    public void The_design_system_itself_contains_no_literal_colours()
    {
        foreach (var file in DesignSystemFiles())
        {
            var source = File.ReadAllText(file);

            Assert.False(
                LiteralColour().IsMatch(source),
                $"{Path.GetRelativePath(ApplicationViews.RepositoryRoot, file)} contains a "
                + "literal colour. Every colour must come from a Shell* brush token.");
        }
    }

    [Fact]
    public void Literal_layout_values_in_application_markup_only_ever_decrease()
    {
        var counted = ApplicationXamlFiles()
            .Sum(file => LiteralLayoutValues(File.ReadAllText(file)).Count());

        Assert.True(
            counted <= LiteralLayoutBudget,
            $"The application's markup now sets {counted} layout values to literals, up from "
            + $"{LiteralLayoutBudget}. Each one is a gap the density, text-scale, and "
            + "corner-radius settings cannot reach. Use a Shell* token, or a component "
            + "that already does.");

        Assert.True(
            counted >= LiteralLayoutBudget - 8,
            $"The application's markup is down to {counted} literal layout values from "
            + $"{LiteralLayoutBudget}. Lower {nameof(LiteralLayoutBudget)} to lock the "
            + "progress in, or the ratchet stops holding.");
    }

    /// <summary>
    /// Shape and tone are separate axes. Conflating them is what produced a
    /// "danger button" that was really a fixed-size icon button, and so a labelled
    /// destructive action whose text was clipped in the shipping application.
    /// </summary>
    [Fact]
    public void No_style_class_conflates_a_control_shape_with_a_tone()
    {
        foreach (var file in ApplicationXamlFiles())
        {
            var source = File.ReadAllText(file);

            Assert.DoesNotContain(
                "DangerButton",
                Regex.Replace(source, "<!--.*?-->", string.Empty, RegexOptions.Singleline),
                StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> LiteralLayoutValues(string source)
    {
        foreach (var axis in LayoutAxes)
        {
            foreach (Match match in Regex.Matches(source, axis + "=\"(?<value>[^\"]*)\""))
            {
                var value = match.Groups["value"].Value;

                // Zero is the absence of a value, not a magic number: there is no
                // token for nothing, and "this card supplies its own inset" has to
                // stay sayable.
                if (!value.StartsWith('{') && !IsZero(value))
                {
                    yield return $"{axis}=\"{value}\"";
                }
            }
        }
    }

    private static bool IsZero(string value) => value
        .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
        .All(part => part is "0");

    /// <summary>
    /// The files that define the system rather than use it.
    /// </summary>
    private static IEnumerable<string> DesignSystemFiles()
    {
        var application = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App");
        yield return Path.Combine(application, "Styles", "DesignSystem.axaml");
        yield return Path.Combine(application, "Views", "DesignSystemGalleryWindow.axaml");
    }

    private static IEnumerable<string> ApplicationXamlFiles() =>
        Directory
            .EnumerateFiles(
                Path.Combine(ApplicationViews.RepositoryRoot, "src", "GhostShell.App"),
                "*.axaml",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex("(Background|Foreground|BorderBrush|Color)=\"#[0-9A-Fa-f]{3,8}\"")]
    private static partial Regex LiteralColour();
}
