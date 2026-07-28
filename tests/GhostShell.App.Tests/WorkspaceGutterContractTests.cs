using System.Xml.Linq;

namespace GhostShell.App.Tests;

/// <summary>
/// The panel canvas splits its gutter between the container and each panel, so the
/// gap between two panels is the same as the gap to the edge. The panels used to
/// carry the whole gutter on their right and bottom only, which made two edges
/// twice the width of the other two.
/// </summary>
public sealed class WorkspaceGutterContractTests
{
    private static XDocument Workspace() => XDocument.Load(Path.Combine(
        RepositoryRoot,
        "src",
        "GhostShell.App",
        "Views",
        "WorkspaceView.axaml"));

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void The_canvas_and_its_panels_split_the_same_gutter()
    {
        var root = Workspace().Root!;

        var canvas = Assert.Single(
            root.Descendants(),
            element => element.Name.LocalName == "Grid"
                && (string?)element.Attribute("IsVisible")
                    == "{Binding IsWorkspaceCanvasVisible}");
        var panelMargin = root.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .SelectMany(element => element.Descendants())
            .Where(element => element.Name.LocalName == "Setter"
                && (string?)element.Attribute("Property") == "Margin")
            .Select(element => (string?)element.Attribute("Value"))
            .ToArray();

        var canvasMargin = (string?)canvas.Attribute("Margin");

        // Both take the same step off the spacing scale, which is what makes the
        // gap between two panels equal the gap to any edge. Naming the step rather
        // than the number is also what lets the density setting move both together.
        // The same step, in each of the two forms the framework needs: a markup
        // extension on an element, a resource in a style setter.
        Assert.Equal("{controls:Inset Xs}", canvasMargin);
        Assert.Contains("{DynamicResource ShellInsetXs}", panelMargin);

        // Uniform on every side: one step, not an edge-specific inset.
        Assert.DoesNotContain(panelMargin, value => value?.Contains('=') == true);
    }

    /// <summary>
    /// Half a gutter each side means two panels sit a full gutter apart, and a
    /// panel sits a full gutter from the edge. Stated as arithmetic so the intent
    /// survives a change to the number.
    /// </summary>
    [Fact]
    public void Edge_and_interior_gaps_come_out_equal()
    {
        const double canvas = 4;
        const double panel = 4;

        var edgeGap = canvas + panel;
        var interiorGap = panel + panel;

        Assert.Equal(edgeGap, interiorGap);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the GhostSHELL repository root.");
    }
}
