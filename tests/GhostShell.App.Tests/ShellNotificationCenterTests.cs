using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

/// <summary>
/// The rule the centre exists to enforce: a request to be noticed leaves a mark
/// unless you were already looking at the panel that made it, and the mark goes
/// when you look.
/// </summary>
public sealed class ShellNotificationCenterTests
{
    [Fact]
    public void A_notification_marks_the_panel_its_tab_and_its_workspace()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");

        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.True(panel.HasAttention);
        Assert.True(tab.HasAttention);
        Assert.True(workspace.HasAttention);
        Assert.True(shell.FlagsRefreshed > 0);
    }

    [Fact]
    public void A_workspace_notification_marks_background_work_without_a_panel()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");

        shell.Center.NotifyWorkspace(workspace);

        Assert.False(panel.HasAttention);
        Assert.False(tab.HasAttention);
        Assert.True(workspace.HasAttention);
        Assert.True(shell.FlagsRefreshed > 0);
    }

    [Fact]
    public void Looking_at_a_workspace_clears_its_workspace_notification()
    {
        var shell = new FakeShell();
        var (workspace, _, _) = shell.AddWorkspace("background");
        shell.Center.NotifyWorkspace(workspace);

        shell.Front = workspace;
        shell.Center.MarkVisibleSeen();

        Assert.False(workspace.HasAttention);
    }

    [Fact]
    public void Workspace_work_finishing_in_the_visible_workspace_leaves_no_mark()
    {
        var shell = new FakeShell();
        var (workspace, _, _) = shell.AddWorkspace("front");
        shell.Front = workspace;

        shell.Center.NotifyWorkspace(workspace);

        Assert.False(workspace.HasAttention);
    }

    /// <summary>
    /// Marking a panel you are already watching would be a dot you could never
    /// clear — it would reappear on the next line of output.
    /// </summary>
    [Fact]
    public void A_notification_from_the_panel_in_front_leaves_no_mark()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("front");
        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);

        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.False(panel.HasAttention);
        Assert.False(workspace.HasAttention);
    }

    /// <summary>
    /// Same panel, same workspace — but nobody is at the keyboard, so "looking
    /// at it" is not true.
    /// </summary>
    [Fact]
    public void A_notification_marks_the_panel_in_front_when_the_window_is_not_focused()
    {
        var shell = new FakeShell { IsFocused = false };
        var (workspace, tab, panel) = shell.AddWorkspace("front");
        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);

        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        Assert.True(panel.HasAttention);
    }

    [Fact]
    public void Looking_at_the_panel_clears_the_whole_chain()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(panel.Id);
        shell.Center.MarkVisibleSeen();

        Assert.False(panel.HasAttention);
        Assert.False(tab.HasAttention);
        Assert.False(workspace.HasAttention);
    }

    /// <summary>
    /// Switching to the workspace is not the same as reading the panel: the
    /// mark belongs to the panel, and a dot that cleared on arrival at the
    /// workspace would send you looking for something already gone.
    /// </summary>
    [Fact]
    public void Arriving_at_the_workspace_on_another_tab_keeps_the_mark()
    {
        var shell = new FakeShell();
        var (workspace, tab, panel) = shell.AddWorkspace("background");
        var otherTab = shell.AddTab(workspace, "other");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        shell.Front = workspace;
        workspace.ActiveTab = otherTab;
        shell.Center.MarkVisibleSeen();

        Assert.True(panel.HasAttention);
        Assert.True(tab.HasAttention);
        Assert.True(workspace.HasAttention);
    }

    /// <summary>
    /// A workspace with two marked panels keeps its dot until both are read —
    /// the aggregate is recomputed rather than counted down.
    /// </summary>
    [Fact]
    public void The_workspace_stays_marked_until_every_panel_is_read()
    {
        var shell = new FakeShell();
        var (workspace, tab, first) = shell.AddWorkspace("background");
        var second = shell.AddPanel(workspace, tab, "second");
        shell.Center.Watch(workspace);
        first.RaiseNotification();
        second.RaiseNotification();

        shell.Front = workspace;
        workspace.ActiveTab = tab;
        tab.ActivatePanel(first.Id);
        shell.Center.MarkVisibleSeen();

        Assert.False(first.HasAttention);
        Assert.True(workspace.HasAttention);

        tab.ActivatePanel(second.Id);
        shell.Center.MarkVisibleSeen();

        Assert.False(workspace.HasAttention);
    }

    [Fact]
    public void Forgetting_a_workspace_stops_its_panels_and_drops_its_mark()
    {
        var shell = new FakeShell();
        var (workspace, _, panel) = shell.AddWorkspace("closing");
        shell.Center.Watch(workspace);
        panel.RaiseNotification();

        shell.Center.Forget(workspace);
        panel.RaiseNotification();

        Assert.False(workspace.HasAttention);
    }

    /// <summary>
    /// A workspace reactivated while it was never closed must not end up with
    /// two subscriptions, which would be harmless for a boolean and a real bug
    /// the moment anything counts.
    /// </summary>
    [Fact]
    public void Watching_a_workspace_twice_subscribes_once()
    {
        var shell = new FakeShell();
        var (workspace, _, panel) = shell.AddWorkspace("reopened");

        shell.Center.Watch(workspace);
        shell.Center.Watch(workspace);

        Assert.Equal(1, panel.SubscriberCount);
    }

    /// <summary>
    /// A panel that can be told to ask for attention, standing in for a
    /// terminal so the rule can be tested without a live session behind it.
    /// </summary>
    private sealed class FakeNotifyingPanel(PanelInstanceId id, string title)
        : RuntimePanelViewModel(id, PanelKind.Terminal, title, "Test"), IPanelNotificationSource
    {
        public event EventHandler<PanelNotificationEvent>? NotificationReceived;

        public int SubscriberCount =>
            NotificationReceived?.GetInvocationList().Length ?? 0;

        public void RaiseNotification() =>
            NotificationReceived?.Invoke(
                this,
                new PanelNotificationEvent(
                    1,
                    PanelNotificationKind.Notification,
                    "Agent",
                    "Waiting for input",
                    DateTimeOffset.UnixEpoch));
    }

    private sealed class FakeShell
    {
        private int _panelCount;

        public FakeShell() =>
            Center = new ShellNotificationCenter(
                () => Front,
                () => IsFocused,
                () => FlagsRefreshed++);

        public ShellNotificationCenter Center { get; }

        public RuntimeWorkspaceViewModel? Front { get; set; }

        public bool IsFocused { get; set; } = true;

        public int FlagsRefreshed { get; private set; }

        public (RuntimeWorkspaceViewModel Workspace, RuntimeTabViewModel Tab, FakeNotifyingPanel Panel)
            AddWorkspace(string name)
        {
            var workspace = new RuntimeWorkspaceViewModel(
                WorkspaceInstanceId.New(),
                name,
                "#B8793A",
                []);
            var tab = AddTab(workspace, $"{name}-tab");
            var panel = AddPanel(workspace, tab, $"{name}-panel");
            return (workspace, tab, panel);
        }

        public RuntimeTabViewModel AddTab(RuntimeWorkspaceViewModel workspace, string name)
        {
            var tab = new RuntimeTabViewModel(new TabInstanceId(name), name, "Test");
            workspace.Tabs.Add(tab);
            return tab;
        }

        public FakeNotifyingPanel AddPanel(
            RuntimeWorkspaceViewModel workspace,
            RuntimeTabViewModel tab,
            string title)
        {
            var panel = new FakeNotifyingPanel(
                new PanelInstanceId($"{title}-{_panelCount++}"),
                title);
            tab.AddPanel(panel);
            Center.Watch(workspace, tab, panel);
            return panel;
        }
    }
}
