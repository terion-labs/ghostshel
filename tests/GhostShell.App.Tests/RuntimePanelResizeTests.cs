using GhostShell.App.ViewModels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

/// <summary>
/// The canvas divided itself evenly and had no way to say otherwise, so panels
/// could not be resized at all — the split was fixed when the layout was chosen
/// and never again. These pin the track weights that replaced the even division.
/// </summary>
public sealed class RuntimePanelResizeTests
{
    private static RuntimeTabViewModel Tab(int panels)
    {
        var tab = new RuntimeTabViewModel(new TabInstanceId("tab"), "Tab", "source");
        for (var index = 0; index < panels; index++)
        {
            tab.AddPanel(new UnavailableRuntimePanelViewModel(
                new PanelInstanceId($"panel-{index}"),
                PanelKind.Terminal,
                $"Panel {index}",
                "LOCAL",
                "unavailable"));
        }

        return tab;
    }

    [Fact]
    public void Tracks_start_evenly_divided()
    {
        var tab = Tab(2);

        Assert.All(tab.ColumnWeights, weight => Assert.Equal(1d / tab.ColumnWeights.Count, weight, 6));
        Assert.Equal(1, tab.ColumnWeights.Sum(), 6);
    }

    [Fact]
    public void Moving_a_boundary_takes_from_one_track_and_gives_to_the_other()
    {
        var tab = Tab(2);
        if (tab.ColumnWeights.Count < 2)
        {
            return;
        }

        var before = tab.ColumnWeights.ToArray();
        Assert.True(tab.MoveColumnSplit(0, 0.1, 0.1));

        Assert.Equal(before[0] + 0.1, tab.ColumnWeights[0], 6);
        Assert.Equal(before[1] - 0.1, tab.ColumnWeights[1], 6);
        Assert.Equal(1, tab.ColumnWeights.Sum(), 6);
    }

    /// <summary>
    /// A drag past the limit stops at it rather than being refused, which is what
    /// makes dragging feel like it is against a wall instead of broken.
    /// </summary>
    [Fact]
    public void A_boundary_stops_at_the_minimum_instead_of_collapsing_a_track()
    {
        var tab = Tab(2);
        if (tab.ColumnWeights.Count < 2)
        {
            return;
        }

        Assert.True(tab.MoveColumnSplit(0, 5, 0.2));

        Assert.Equal(0.8, tab.ColumnWeights[0], 6);
        Assert.Equal(0.2, tab.ColumnWeights[1], 6);
    }

    /// <summary>
    /// Closing a panel used to leave its track behind, so the survivor kept half
    /// the canvas and the next panel was appended past the hole — which is why
    /// adding one after closing one divided the canvas into three with a gap.
    /// </summary>
    [Fact]
    public void Closing_a_panel_gives_its_space_to_the_ones_that_remain()
    {
        var tab = Tab(2);
        var first = tab.Panels[0].Id;

        Assert.True(tab.RemovePanel(first));

        var survivor = Assert.Single(tab.Panels);
        Assert.Equal(1, tab.Columns);
        Assert.Equal(1, tab.Rows);
        Assert.Equal(0, survivor.LayoutColumn);
        Assert.Equal(0, survivor.LayoutRow);
        Assert.Equal(1, survivor.LayoutColumnSpan);
        Assert.Equal(1, survivor.LayoutRowSpan);
    }

    [Fact]
    public void A_panel_added_after_a_close_lands_beside_the_survivor()
    {
        var tab = Tab(2);
        Assert.True(tab.RemovePanel(tab.Panels[0].Id));

        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            new PanelInstanceId("panel-added"),
            PanelKind.Terminal,
            "Added",
            "LOCAL",
            "unavailable"));

        // Two panels, two tracks, no empty one between them.
        Assert.Equal(2, tab.Panels.Count);
        Assert.Equal(2, tab.Rows * tab.Columns);
        Assert.Equal(
            new[] { 0, 1 },
            tab.Panels.Select(panel => panel.LayoutRow * tab.Columns + panel.LayoutColumn)
                .Order()
                .ToArray());
    }

    [Fact]
    public void A_boundary_that_does_not_exist_is_refused()
    {
        var tab = Tab(2);

        Assert.False(tab.MoveColumnSplit(-1, 0.1, 0.1));
        Assert.False(tab.MoveColumnSplit(99, 0.1, 0.1));
        Assert.False(tab.MoveColumnSplit(0, double.NaN, 0.1));
    }
}
