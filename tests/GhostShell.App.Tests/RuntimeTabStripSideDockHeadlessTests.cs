using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.VisualTree;
using GhostShell.App.Views.Components;

namespace GhostShell.App.Tests;

/// <summary>
/// The side-docked tab strip's box model, measured rather than eyeballed: rows
/// fill the strip's width, and the close button holds the row's trailing edge
/// no matter how long the title is. This geometry shipped broken twice because
/// nothing measured it.
/// </summary>
public sealed class RuntimeTabStripSideDockHeadlessTests
{
    private sealed record FakeTab(string Title, bool IsActive, bool CanClose, bool HasAttention);

    [Fact]
    public Task Side_docked_tabs_fill_the_strip_and_pin_their_close_buttons() =>
        RunHeadlessAsync(() =>
        {
            var strip = new RuntimeTabStripView
            {
                Orientation = Orientation.Vertical,
                Tabs = new object[]
                {
                    new FakeTab("production-api", IsActive: true, CanClose: true, HasAttention: false),
                    new FakeTab("db", IsActive: false, CanClose: true, HasAttention: false),
                },
            };
            var window = new Window
            {
                Width = 190,
                Height = 400,
                Content = strip,
            };
            window.Show();
            window.UpdateLayout();

            var rows = strip.GetVisualDescendants()
                .OfType<Grid>()
                .Where(grid => grid.Classes.Contains("RuntimeTabDropTarget"))
                .ToArray();
            Assert.Equal(2, rows.Length);

            var scroll = strip.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .First();
            foreach (var row in rows)
            {
                // The row fills the strip's width: a content-sized row leaves a
                // ragged right edge and a close button that trails the title.
                Assert.True(
                    Math.Abs(strip.Bounds.Width - row.Bounds.Width) <= 2,
                    $"strip={strip.Bounds.Width} scroll={scroll.Bounds.Width} "
                    + $"extent={scroll.Extent.Width} viewport={scroll.Viewport.Width} "
                    + $"h={scroll.HorizontalScrollBarVisibility} v={scroll.VerticalScrollBarVisibility} "
                    + $"row={row.Bounds.Width}");

                var close = Assert.Single(
                    row.GetVisualDescendants().OfType<Button>(),
                    button => button.Classes.Contains("IconButton"));
                var closeRight = close.TranslatePoint(
                    new Point(close.Bounds.Width, 0),
                    row);
                Assert.NotNull(closeRight);
                // The close column is the row's last 20 device pixels.
                Assert.InRange(row.Bounds.Width - closeRight.Value.X, 0, 3);
            }

            // Both rows end at the same edge — the long and the short title.
            Assert.InRange(
                Math.Abs(rows[0].Bounds.Width - rows[1].Bounds.Width),
                0,
                1);

            // The reorder handle keeps its grab column; a collapsed handle is
            // a 1px sliver nobody can drag.
            var handle = rows[0].GetVisualDescendants()
                .OfType<Border>()
                .First(border => ToolTip.GetTip(border) as string == "Drag to reorder tab");
            var chip = rows[0].GetVisualDescendants()
                .OfType<Button>()
                .First(button => button.Classes.Contains("RuntimeTabActivator"));
            var title = chip.GetVisualDescendants()
                .OfType<TextBlock>()
                .First();
            var titleTop = title.TranslatePoint(default, rows[0]);
            Assert.NotNull(titleTop);
            Assert.True(
                handle.Bounds.Width >= 16,
                $"handle={handle.Bounds.Width}x{handle.Bounds.Height} "
                + $"row={rows[0].Bounds.Height} chip={chip.Bounds.Height} "
                + $"titleY={titleTop.Value.Y} titleH={title.Bounds.Height}");
            // The title sits on the row's vertical centre.
            var titleCentreOffset = titleTop.Value.Y + (title.Bounds.Height / 2)
                - (rows[0].Bounds.Height / 2);
            Assert.True(
                Math.Abs(titleCentreOffset) <= 1.5,
                $"title centre off by {titleCentreOffset} "
                + $"(row={rows[0].Bounds.Height} chip={chip.Bounds.Height} "
                + $"titleY={titleTop.Value.Y} titleH={title.Bounds.Height})");
            window.Close();
            return Task.CompletedTask;
        });

    private static async Task RunHeadlessAsync(Func<Task> assertion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    await assertion();
                    return true;
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
