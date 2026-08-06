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
    /// The gap differs corner to corner, so the corners do too — which is the
    /// whole point of measuring rather than subtracting one number everywhere.
    /// </summary>
    [Fact]
    public void Each_corner_answers_to_its_own_gap()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(4, 10),
            size: new Size(380, 268),
            minimumRadius: 2);

        Assert.NotNull(derived);
        // Left 4, top 10, right 16, bottom 22: each corner takes the smaller
        // of the two edges meeting there.
        Assert.Equal(22, derived!.Value.TopLeft);
        Assert.Equal(16, derived.Value.TopRight);
        Assert.Equal(10, derived.Value.BottomRight);
        Assert.Equal(22, derived.Value.BottomLeft);
    }

    /// <summary>
    /// Sharing the corner is the whole premise. An element further from an
    /// edge than the radius itself is not near that corner, and stepping in by
    /// that distance would square it — so the rule declines instead.
    /// </summary>
    [Fact]
    public void An_element_further_out_than_the_radius_is_left_alone() =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 10,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(40, 40),
            size: new Size(320, 220),
            minimumRadius: 3));

    [Fact]
    public void A_notice_pinned_to_one_corner_keeps_the_radius_it_was_given() =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(1400, 900),
            offsetInContainer: new Point(880, 840),
            size: new Size(504, 44),
            minimumRadius: 2));

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
