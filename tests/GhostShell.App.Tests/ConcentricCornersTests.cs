using Avalonia;
using GhostShell.App.Controls;

namespace GhostShell.App.Tests;

public sealed class ConcentricCornersTests
{
    [Fact]
    public void An_evenly_inset_child_steps_in_by_its_inset()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(8, 8),
            size: new Size(384, 284),
            minimumRadius: 2);

        Assert.NotNull(derived);
        Assert.Equal(new CornerRadius(18), derived!.Value);
    }

    [Fact]
    public void A_child_flush_against_its_container_keeps_the_whole_radius()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: default,
            size: new Size(400, 300),
            minimumRadius: 2);

        Assert.Equal(new CornerRadius(26), derived!.Value);
    }

    /// <summary>
    /// One surface one distance inside another takes one radius. Corner by
    /// corner gave a tall element two tight corners and two round ones, which
    /// makes a single shape read as two.
    /// </summary>
    [Fact]
    public void The_closest_edge_decides_every_corner()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(4, 10),
            size: new Size(380, 268),
            minimumRadius: 2);

        // Left 4, top 10, right 16, bottom 22: the nearest edge is 4 away.
        Assert.Equal(new CornerRadius(22), derived!.Value);
    }

    /// <summary>
    /// A sidebar under a tab strip: far from the window's top, hard against
    /// its left and bottom. The distance that counts is the one it actually
    /// stands off by, not the one the tab strip put above it.
    /// </summary>
    [Fact]
    public void A_sidebar_below_the_chrome_follows_the_edge_it_hugs()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(1400, 900),
            offsetInContainer: new Point(8, 120),
            size: new Size(200, 772),
            minimumRadius: 2);

        Assert.Equal(new CornerRadius(18), derived!.Value);
    }

    [Fact]
    public void An_element_standing_off_further_than_the_radius_is_left_alone() =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 10,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(40, 40),
            size: new Size(320, 220),
            minimumRadius: 3));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void A_square_container_has_nothing_to_derive_from(double outerRadius) =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius,
            new Size(400, 300),
            default,
            new Size(100, 100),
            minimumRadius: 2));

    [Fact]
    public void An_unarranged_element_is_left_alone() =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: default,
            size: default,
            minimumRadius: 2));
}
