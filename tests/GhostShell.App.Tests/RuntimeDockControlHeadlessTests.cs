using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views;
using GhostShell.App.Views.Components;
using GhostShell.App.Views.RuntimePanels;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class RuntimeDockControlHeadlessTests
{
    [Fact]
    public Task Runtime_tab_is_initialized_and_published_immediately() =>
        RunHeadlessAsync(() =>
        {
            var tab = NewTab();
            var canvas = new RuntimeDockControl
            {
                RuntimeTab = tab,
                InitializeFactory = true,
                InitializeLayout = false,
            };

            Assert.Same(tab.DockFactory, canvas.Factory);
            Assert.Same(tab.DockLayout, canvas.Layout);
            Assert.Same(tab.DockFactory, tab.DockLayout.Factory);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Changing_tabs_publishes_the_initialized_replacement_layout() =>
        RunHeadlessAsync(() =>
        {
            var first = NewTab();
            var second = NewTab();
            var canvas = new RuntimeDockControl
            {
                RuntimeTab = first,
                InitializeFactory = true,
                InitializeLayout = false,
            };

            canvas.RuntimeTab = second;

            Assert.Same(second.DockFactory, canvas.Factory);
            Assert.Same(second.DockLayout, canvas.Layout);
            Assert.Same(second.DockFactory, second.DockLayout.Factory);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Clearing_the_runtime_tab_clears_the_dock_model() =>
        RunHeadlessAsync(() =>
        {
            var canvas = new RuntimeDockControl
            {
                RuntimeTab = NewTab(),
                InitializeFactory = true,
                InitializeLayout = false,
            };

            canvas.RuntimeTab = null;

            Assert.Null(canvas.Layout);
            Assert.Null(canvas.Factory);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Presentation_discards_documents_that_have_no_live_runtime_panel() =>
        RunHeadlessAsync(() =>
        {
            var tab = NewTab();
            var staleDocument = new Document
            {
                Id = "stale-recovery-panel",
                Title = "Stale recovery panel",
            };
            var staleLeaf = new DocumentDock
            {
                Id = "stale-recovery-leaf",
                ActiveDockable = staleDocument,
                VisibleDockables = tab.DockFactory.CreateList<IDockable>(staleDocument),
            };
            tab.DockFactory.AddDockable(tab.DockLayout, staleLeaf);
            tab.DockLayout.ActiveDockable = staleLeaf;

            tab.InitializeDockLayoutForPresentation();

            var dockables = EnumerateDockables(tab.DockLayout).ToArray();
            Assert.DoesNotContain(staleDocument, dockables);
            Assert.Contains(
                dockables.OfType<Document>(),
                document => ReferenceEquals(document.Context, tab.ActivePanel));
            Assert.NotSame(staleLeaf, tab.DockLayout.ActiveDockable);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Runtime_canvas_theme_materializes_the_root_without_a_deferred_host() =>
        RunHeadlessAsync(() =>
        {
            var resources = new WorkspaceView().Resources;
            var theme = Assert.IsType<ControlTheme>(resources["RuntimeDockControlTheme"]);
            var tab = NewTab();
            var canvas = new RuntimeDockControl
            {
                Theme = theme,
                RuntimeTab = tab,
                InitializeFactory = true,
                InitializeLayout = false,
            };
            var window = new Window
            {
                Width = 800,
                Height = 600,
                Content = canvas,
            };
            foreach (var template in new MainWindow().DataTemplates)
            {
                window.DataTemplates.Add(template);
            }

            window.Show();
            try
            {
                canvas.ApplyTemplate();
                window.UpdateLayout();

                var contentHost = Assert.Single(
                    canvas.GetVisualDescendants().OfType<ContentControl>(),
                    control => string.Equals(control.Name, "PART_ContentControl", StringComparison.Ordinal));
                Assert.IsType<ContentControl>(contentHost);
                Assert.Contains(canvas.GetVisualDescendants(), visual => visual is RootDockControl);
                var rootContentHost = Assert.Single(
                    canvas.GetVisualDescendants().OfType<ContentControl>(),
                    control => string.Equals(control.Name, "PART_MainContent", StringComparison.Ordinal));
                Assert.IsType<ContentControl>(rootContentHost);
                var panelContentHost = Assert.Single(
                    canvas.GetVisualDescendants().OfType<ContentControl>(),
                    control => string.Equals(control.Name, "PART_ContentPresenter", StringComparison.Ordinal));
                Assert.IsType<RuntimePanelContentControl>(panelContentHost);
                Assert.Same(tab.ActivePanel, panelContentHost.Content);
                Assert.Contains(
                    canvas.GetVisualDescendants(),
                    visual => visual is UnavailableRuntimePanelView);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    private static RuntimeTabViewModel NewTab()
    {
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Local terminal",
            "test");
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKind.Terminal,
            "Local terminal",
            "LOCAL",
            "Unavailable in this test."));
        return tab;
    }

    private static IEnumerable<IDockable> EnumerateDockables(IDockable root)
    {
        var pending = new Stack<IDockable>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is IDock { VisibleDockables: { } children })
            {
                foreach (var child in children)
                {
                    pending.Push(child);
                }
            }
        }
    }

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
