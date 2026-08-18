using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class WorkspaceEditorViewModelTests
{
    [Fact]
    public void Workspace_can_enable_and_persist_its_own_agent_configuration()
    {
        var profile = new AiProviderProfileDescriptor(
            new AiProviderProfileId("provider-openai"),
            "OpenAI",
            AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1/"),
            "gpt-5.6-terra",
            0,
            IsEnabled: true,
            RequiresCredential: true);
        using var editor = new WorkspaceEditorViewModel(
            Workspace([]),
            3,
            [],
            [],
            [],
            [],
            [profile]);

        editor.AgentPolicy.IsEnabled = true;
        editor.AgentPolicy.SelectedProvider = Assert.Single(
            editor.AgentPolicy.ProviderOptions);
        var workspaceModel = editor.AgentPolicy.AgentTaskModelOptions
            .Single(option => option.Selection == new AgentModelSelection(
                profile.Id.Value,
                profile.DefaultModel));
        editor.AgentPolicy.SelectedCompactionModel = workspaceModel;
        editor.AgentPolicy.SelectedTitleModel = editor.AgentPolicy.TitleModelOptions
            .Single(option => option.Selection == workspaceModel.Selection);
        editor.AgentPolicy.SystemPrompt = "Use this workspace's build conventions.";
        editor.AgentPolicy.Capabilities.Single(item =>
            item.Capability == AgentCapability.RunCommands).SelectedPermission =
            AgentPermission.Auto;

        var saved = editor.CreateSaveRequest().Definition;

        var policy = Assert.IsType<AgentPolicy>(saved.AgentPolicyOverride);
        Assert.Equal("provider-openai", policy.Provider);
        Assert.Equal("gpt-5.6-terra", policy.Model);
        Assert.Equal(workspaceModel.Selection, policy.CompactionModel);
        Assert.Equal(workspaceModel.Selection, policy.TitleModel);
        Assert.Equal(
            "Use this workspace's build conventions.",
            policy.SystemPrompt);
        Assert.Equal(
            AgentPermission.Auto,
            policy.GetPermission(AgentCapability.RunCommands));
    }

    [Fact]
    public void Save_request_preserves_durable_state_and_mixed_entry_order()
    {
        var connection = LocalConnection("local");
        var layout = Layout("single", "main");
        var screen = Screen("deploy", layout, connection.Id);
        var tabPanel = TerminalPanel("scratch-terminal", "main", connection.Id, "/work", "git status");
        var original = Workspace(
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("connection-entry"),
                    connection.Id,
                    "Shell"),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("screen-entry"),
                    screen.Id),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("tab-entry"),
                    "Scratch",
                    layout.Id,
                    [tabPanel]),
            ],
            AgentPolicy.Default,
            icon: "terminal");
        using var editor = new WorkspaceEditorViewModel(
            original,
            17,
            [connection],
            [screen],
            [layout]);

        editor.Name = "Production";
        editor.Description = "Operations workspace";
        editor.Accent = "#112233";
        editor.Icon = "server";
        editor.Entries[0].Alias = "Primary shell";
        Assert.True(editor.MoveEntry(new WorkspaceEntryId("tab-entry"), 0).IsSuccess);

        var request = editor.CreateSaveRequest();

        Assert.Equal(17, request.ExpectedRevision);
        Assert.Equal(original.Id, request.Definition.Id);
        Assert.Equal(original.SchemaVersion, request.Definition.SchemaVersion);
        Assert.Equal(
            original.AgentPolicyOverride?.Provider,
            request.Definition.AgentPolicyOverride?.Provider);
        Assert.Equal(
            original.AgentPolicyOverride?.Model,
            request.Definition.AgentPolicyOverride?.Model);
        Assert.Equal(
            original.AgentPolicyOverride?.Permissions.OrderBy(pair => pair.Key),
            request.Definition.AgentPolicyOverride?.Permissions.OrderBy(pair => pair.Key));
        Assert.Equal("server", request.Definition.Icon);
        Assert.Equal("#112233", request.Definition.Accent);
        Assert.Equal(
            ["tab-entry", "connection-entry", "screen-entry"],
            request.Definition.Entries.Select(entry => entry.Id.Value));
        var savedPanel = Assert.IsType<WorkspaceEntry.Tab>(request.Definition.Entries[0]).Panels[0];
        Assert.Equal(tabPanel.Id, savedPanel.Id);
        Assert.Equal(tabPanel.Startup.Location, savedPanel.Startup.Location);
        Assert.Equal(tabPanel.Startup.Commands, savedPanel.Startup.Commands);
        Assert.Equal(
            "Primary shell",
            Assert.IsType<WorkspaceEntry.ConnectionReference>(request.Definition.Entries[1]).Alias);
    }

    [Fact]
    public void Connection_and_saved_screen_entries_can_be_added_removed_and_reordered()
    {
        var connection = LocalConnection("local");
        var layout = Layout("single", "main");
        var screen = Screen("deploy", layout, connection.Id);
        using var editor = new WorkspaceEditorViewModel(
            Workspace([]),
            4,
            [connection],
            [screen],
            [layout]);

        var connectionResult = editor.AddConnection(connection.Id, "Local shell");
        var screenResult = editor.AddSavedScreen(screen.Id, "Deploy view");
        Assert.True(connectionResult.IsSuccess);
        Assert.True(screenResult.IsSuccess);
        Assert.True(editor.MoveEntry(screenResult.EntryId!.Value, 0).IsSuccess);
        Assert.True(editor.RemoveEntry(connectionResult.EntryId!.Value).IsSuccess);

        var request = editor.CreateSaveRequest();

        var saved = Assert.Single(request.Definition.Entries);
        var reference = Assert.IsType<WorkspaceEntry.ScreenReference>(saved);
        Assert.Equal(screen.Id, reference.ScreenId);
        Assert.Equal("Deploy view", reference.Alias);
        Assert.Empty(editor.ConnectionEntries);
        Assert.Single(editor.SavedScreenEntries);
    }

    [Fact]
    public void Missing_references_stay_visible_until_each_one_is_repaired()
    {
        var availableConnection = LocalConnection("available");
        var missingConnectionId = new ConnectionId("removed-connection");
        var layout = Layout("single", "main");
        var availableScreen = Screen("available-screen", layout, availableConnection.Id);
        var workspace = Workspace(
        [
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("connection"),
                missingConnectionId),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("screen"),
                new ScreenId("removed-screen")),
            new WorkspaceEntry.Tab(
                new WorkspaceEntryId("tab"),
                "Scratch",
                layout.Id,
                [TerminalPanel("terminal", "main", missingConnectionId)]),
        ]);
        using var editor = new WorkspaceEditorViewModel(
            workspace,
            9,
            [availableConnection],
            [availableScreen],
            [layout]);

        Assert.True(editor.HasMissingReferences);
        Assert.Equal(3, editor.MissingReferenceCount);
        Assert.All(editor.Entries, entry => Assert.Equal("Repair required", entry.ReferenceStatus));
        Assert.Throws<InvalidOperationException>(() => editor.CreateSaveRequest());

        editor.Entries.Single(entry => entry.IsConnection).SelectedConnection =
            editor.ConnectionOptions.Single(option => option.Id == availableConnection.Id);
        editor.Entries.Single(entry => entry.IsSavedScreen).SelectedScreen =
            editor.ScreenOptions.Single(option => option.Id == availableScreen.Id);
        editor.Entries.Single(entry => entry.IsWorkspaceTab).Tab!.Panels[0].SelectedConnection =
            editor.ConnectionOptions.Single(option => option.Id == availableConnection.Id);

        Assert.False(editor.HasMissingReferences);
        Assert.True(editor.IsValid);
        var saved = editor.CreateSaveRequest().Definition;
        Assert.Equal(
            availableConnection.Id,
            Assert.IsType<WorkspaceEntry.ConnectionReference>(saved.Entries[0]).ConnectionId);
        Assert.Equal(
            availableScreen.Id,
            Assert.IsType<WorkspaceEntry.ScreenReference>(saved.Entries[1]).ScreenId);
    }

    [Fact]
    public void Workspace_only_tab_can_change_layout_and_fill_new_slots()
    {
        var connection = LocalConnection("local");
        var single = Layout("single", "main");
        var split = Layout("split", "main", "secondary");
        var workspace = Workspace(
        [
            new WorkspaceEntry.Tab(
                new WorkspaceEntryId("tab"),
                "Scratch",
                single.Id,
                [TerminalPanel("terminal", "main", connection.Id)]),
        ]);
        using var editor = new WorkspaceEditorViewModel(
            workspace,
            2,
            [connection],
            [],
            [single, split]);
        var tab = Assert.Single(editor.WorkspaceTabEntries).Tab!;

        tab.Name = "Diagnostics";
        tab.SelectedLayout = editor.LayoutOptions.Single(option => option.Id == split.Id);
        Assert.False(editor.IsValid);
        Assert.True(tab.CanAddPanel);
        Assert.True(tab.AddPanel(ScreenPanelKind.Terminal));
        Assert.False(tab.CanAddPanel);
        Assert.True(tab.MovePanel(tab.Panels[1].Id, 0));

        var saved = Assert.IsType<WorkspaceEntry.Tab>(
            Assert.Single(editor.CreateSaveRequest().Definition.Entries));
        Assert.Equal("Diagnostics", saved.Name);
        Assert.Equal(split.Id, saved.LayoutId);
        Assert.Equal(["secondary", "main"], saved.Panels.Select(panel => panel.SlotId.Value));
    }

    [Fact]
    public void Workspace_only_tab_persists_the_startup_delivery_failure_policy()
    {
        var connection = LocalConnection("local");
        var layout = Layout("single-policy", "main");
        var workspace = Workspace(
        [
            new WorkspaceEntry.Tab(
                new WorkspaceEntryId("tab"),
                "Scratch",
                layout.Id,
                [TerminalPanel("terminal", "main", connection.Id)]),
        ]);
        using var editor = new WorkspaceEditorViewModel(
            workspace,
            2,
            [connection],
            [],
            [layout]);
        var panel = Assert.Single(
            Assert.Single(editor.WorkspaceTabEntries).Tab!.Panels);

        Assert.Same(
            StartupCommandDeliveryFailurePolicyOption.RetryWhileLive,
            panel.SelectedDeliveryFailurePolicy);

        panel.SelectedDeliveryFailurePolicy =
            StartupCommandDeliveryFailurePolicyOption.StopAfterFirstDeliveryFailure;

        var savedTab = Assert.IsType<WorkspaceEntry.Tab>(
            Assert.Single(editor.CreateSaveRequest().Definition.Entries));
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            Assert.Single(savedTab.Panels).Startup.DeliveryFailurePolicy);
    }

    [Fact]
    public void Saved_screen_can_be_copied_into_an_independent_workspace_only_tab()
    {
        var connection = LocalConnection("local");
        var layout = Layout("single", "main");
        var screen = Screen("deploy", layout, connection.Id);
        using var editor = new WorkspaceEditorViewModel(
            Workspace([]),
            6,
            [connection],
            [screen],
            [layout]);

        var result = editor.AddWorkspaceTabFromScreen(screen.Id, "Pinned deploy");
        Assert.True(result.IsSuccess);
        var tab = Assert.Single(editor.WorkspaceTabEntries).Tab!;
        tab.Panels[0].Title = "Workspace copy";

        var saved = Assert.IsType<WorkspaceEntry.Tab>(
            Assert.Single(editor.CreateSaveRequest().Definition.Entries));
        Assert.Equal("Pinned deploy", saved.Name);
        Assert.Equal("Workspace copy", saved.Panels[0].Title);
        Assert.Equal("Terminal", screen.Panels[0].Title);
    }

    [Fact]
    public void Invalid_name_color_and_icon_block_save_with_actionable_issues()
    {
        using var editor = new WorkspaceEditorViewModel(
            Workspace([]),
            1,
            [],
            [],
            []);

        editor.Name = " ";
        editor.Accent = "orange";
        editor.Color = "also not a colour";
        editor.Icon = "Bad Icon";

        Assert.False(editor.IsValid);
        Assert.False(editor.CanSave);
        Assert.Contains(editor.ValidationIssues, issue => issue.Code == DefinitionValidationCode.Required);
        // The colour and the accent are separate fields, so a complaint about one
        // must name which one: "the workspace colour is wrong" pointing at the
        // accent is a message that sends you to the wrong control.
        Assert.Contains(editor.ValidationIssues, issue => issue.Message.Contains("accent"));
        Assert.Contains(editor.ValidationIssues, issue => issue.Message.Contains("color"));
        Assert.Contains(editor.ValidationIssues, issue => issue.Message.Contains("icon"));
        Assert.Throws<InvalidOperationException>(() => editor.CreateSaveRequest());
    }

    /// <summary>
    /// The colour marks the workspace; the accent retints the shell. Saving one
    /// as the other would make choosing a tab colour silently change the theme.
    /// </summary>
    [Fact]
    public void The_identity_colour_and_the_accent_are_saved_as_separate_fields()
    {
        using var editor = new WorkspaceEditorViewModel(Workspace([]), 3, [], [], []);

        editor.Color = "#5B8FD1";
        editor.Accent = "#5FA97A";

        var saved = editor.CreateSaveRequest().Definition;
        Assert.Equal("#5B8FD1", saved.Color);
        Assert.Equal("#5FA97A", saved.Accent);
    }

    /// <summary>
    /// A workspace with no colour of its own still has to be drawn, and drawn as
    /// something recognisable rather than as a hole.
    /// </summary>
    [Fact]
    public void A_workspace_with_no_colour_falls_back_to_its_accent()
    {
        using var editor = new WorkspaceEditorViewModel(Workspace([]), 3, [], [], []);

        Assert.Equal(string.Empty, editor.Color);
        Assert.Equal("#B8793A", editor.EffectiveColor);

        editor.Accent = string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(editor.EffectiveColor));

        editor.Color = "#5B8FD1";
        Assert.Equal("#5B8FD1", editor.EffectiveColor);
    }

    /// <summary>
    /// The two swatch rows answer different questions, so they must not agree
    /// by accident: a workspace with a colour and no accent has to show one
    /// row marked and the other empty, or the accent row is claiming a value
    /// the workspace does not have.
    /// </summary>
    [Fact]
    public void A_colour_without_an_accent_marks_only_the_colour_row()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("data"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Data",
            null,
            accent: null,
            [],
            color: "#5B8FD1");
        using var editor = new WorkspaceEditorViewModel(workspace, 3, [], [], []);

        Assert.Equal("#5B8FD1", editor.EffectiveColor);
        Assert.Single(editor.ColorChoices, choice => choice.IsSelected);
        Assert.Equal("#5B8FD1", editor.ColorChoices.Single(choice => choice.IsSelected).Hex);
        Assert.DoesNotContain(editor.AccentChoices, choice => choice.IsSelected);
    }

    /// <summary>
    /// The rail is how you move between workspaces, so the one you are editing
    /// has to be in it — including the one that does not exist yet.
    /// </summary>
    [Fact]
    public void The_rail_marks_the_open_workspace_and_includes_an_unsaved_one()
    {
        var other = new WorkspaceDefinition(
            new WorkspaceId("other"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Other",
            null,
            null,
            [new WorkspaceEntry.ScreenReference(new WorkspaceEntryId("e"), new ScreenId("s"))]);
        using var saved = new WorkspaceEditorViewModel(Workspace([]), 3, [], [], []);
        using var unsaved = WorkspaceEditorViewModel.CreateNew([], [], []);

        saved.SetPeers([Workspace([]), other]);
        unsaved.SetPeers([Workspace([]), other]);

        Assert.Equal(2, saved.PeerCount);
        Assert.Single(saved.Peers, peer => peer.IsCurrent);
        Assert.Equal(saved.Id, saved.Peers.Single(peer => peer.IsCurrent).Id);
        Assert.Equal("1 screen", saved.Peers.Single(peer => peer.Id == other.Id).Summary);
        Assert.Equal("Empty", saved.Peers.Single(peer => peer.Id == saved.Id).Summary);

        Assert.Equal(3, unsaved.PeerCount);
        Assert.Equal(unsaved.Id, unsaved.Peers.Single(peer => peer.IsCurrent).Id);
    }

    /// <summary>
    /// A tab row has to say two things the name does not: what it opens, and
    /// whether editing it elsewhere changes this workspace.
    /// </summary>
    [Fact]
    public void A_tab_row_names_what_it_opens_without_repeating_its_own_heading()
    {
        var connection = LocalConnection("local");
        var layout = Layout("single", "main");
        var screen = Screen("deploy", layout, connection.Id);
        using var editor = new WorkspaceEditorViewModel(
            Workspace(
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("connection-entry"),
                    connection.Id,
                    "Local"),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("tab-entry"),
                    "Scratch",
                    layout.Id,
                    [TerminalPanel("p", "main", connection.Id)]),
            ]),
            3,
            [connection],
            [screen],
            [layout]);

        var reference = editor.Entries[0];
        var workspaceOnly = editor.Entries[1];

        // The alias repeats the connection's name, so the row does not print it
        // twice; giving it a different alias brings the real name back.
        Assert.Equal("Local", reference.DisplayName);
        Assert.Equal("Local", reference.Detail);
        reference.Alias = "Primary shell";
        Assert.Equal("Local · Local", reference.Detail);

        Assert.Equal("Saved", reference.BadgeLabel);
        Assert.False(reference.IsWorkspaceOnly);
        Assert.Equal("Workspace-only", workspaceOnly.BadgeLabel);
        Assert.True(workspaceOnly.IsWorkspaceOnly);
    }

    /// <summary>
    /// The icon row shows a shortlist until asked for everything — but a
    /// workspace whose icon is not on the shortlist must still see its own.
    /// </summary>
    [Fact]
    public void The_icon_row_always_contains_the_workspace_own_icon()
    {
        using var editor = new WorkspaceEditorViewModel(
            Workspace([], icon: "wrench"),
            3,
            [],
            [],
            []);

        Assert.Contains(editor.IconChoices, choice => choice.Id == "wrench");
        Assert.True(editor.IconChoices.Single(choice => choice.Id == "wrench").IsSelected);
        Assert.True(editor.IconChoices.Count < editor.IconCount);

        editor.ShowAllIcons = true;
        Assert.Equal(editor.IconCount, editor.IconChoices.Count);
    }

    [Fact]
    public void Reset_restores_original_snapshot_and_clears_dirty_cancel_prompt()
    {
        var connection = LocalConnection("local");
        var layout = Layout("single", "main");
        var screen = Screen("deploy", layout, connection.Id);
        var original = Workspace(
        [
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("connection"),
                connection.Id,
                "Original alias"),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("screen"),
                screen.Id),
        ],
        icon: "terminal");
        using var editor = new WorkspaceEditorViewModel(
            original,
            3,
            [connection],
            [screen],
            [layout]);

        Assert.Equal(WorkspaceEditorCancelDisposition.Close, editor.RequestCancel());
        editor.Name = "Changed";
        editor.Icon = "server";
        editor.Entries[0].Alias = "Changed alias";
        Assert.True(editor.MoveEntry(new WorkspaceEntryId("screen"), 0).IsSuccess);
        Assert.Equal(WorkspaceEditorCancelDisposition.ConfirmDiscard, editor.RequestCancel());

        editor.Reset();

        Assert.False(editor.IsDirty);
        Assert.Equal(WorkspaceEditorCancelDisposition.Close, editor.RequestCancel());
        Assert.Equal(original.Name, editor.Name);
        Assert.Equal(original.Icon, editor.Icon);
        Assert.Equal(
            original.Entries.Select(entry => entry.Id),
            editor.Entries.Select(entry => entry.Id));
        Assert.Equal("Original alias", editor.Entries[0].Alias);
    }

    [Fact]
    public void New_workspace_is_saveable_without_a_revision()
    {
        using var editor = WorkspaceEditorViewModel.CreateNew([], [], [], name: "Personal");

        Assert.True(editor.IsNew);
        Assert.False(editor.IsDirty);
        Assert.True(editor.CanSave);
        var request = editor.CreateSaveRequest();
        Assert.Null(request.ExpectedRevision);
        Assert.Equal("Personal", request.Definition.Name);
        Assert.Equal(WorkspaceDefinition.DefaultIcon, request.Definition.Icon);
    }

    [Fact]
    public void Autosave_toggle_marks_dirty_and_persists_in_the_save_request()
    {
        var connection = LocalConnection("local");
        var layout = Layout("single", "main");
        using var editor = new WorkspaceEditorViewModel(
            Workspace([]),
            4,
            [connection],
            [],
            [layout]);

        Assert.False(editor.AutoSave);
        Assert.False(editor.IsDirty);

        editor.AutoSave = true;

        Assert.True(editor.IsDirty);
        Assert.True(editor.CreateSaveRequest().Definition.AutoSave);

        editor.Reset();

        Assert.False(editor.AutoSave);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Browser_profile_override_can_be_selected_saved_and_reset()
    {
        using var editor = new WorkspaceEditorViewModel(
            Workspace([], browserProfile: WorkspaceBrowserProfileMode.Shared),
            4,
            [],
            [],
            []);
        editor.SelectedBrowserProfile = Assert.Single(
            editor.BrowserProfileOptions,
            option => option.Mode == WorkspaceBrowserProfileMode.Isolated);

        Assert.True(editor.IsDirty);
        Assert.Equal(
            WorkspaceBrowserProfileMode.Isolated,
            editor.CreateSaveRequest().Definition.BrowserProfileOverride);

        editor.Reset();

        Assert.False(editor.IsDirty);
        Assert.Equal(
            WorkspaceBrowserProfileMode.Shared,
            editor.SelectedBrowserProfile.Mode);
    }

    private static WorkspaceDefinition Workspace(
        IReadOnlyList<WorkspaceEntry> entries,
        AgentPolicy? policy = null,
        string icon = WorkspaceDefinition.DefaultIcon,
        WorkspaceBrowserProfileMode? browserProfile = null) => new(
        new WorkspaceId("workspace"),
        WorkspaceDefinition.CurrentSchemaVersion,
        "Workspace",
        "Description",
        "#B8793A",
        entries,
        policy,
        icon,
        browserProfileOverride: browserProfile);

    private static ConnectionProfile LocalConnection(string id) => new(
        new ConnectionId(id),
        ConnectionProfile.CurrentSchemaVersion,
        "Local",
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private static LayoutDefinition Layout(string id, params string[] slots) => new(
        new LayoutId(id),
        LayoutDefinition.CurrentSchemaVersion,
        id,
        new LayoutGrid(slots.Length, 1),
        slots.Select((slot, index) => new LayoutSlotDefinition(
            new LayoutSlotId(slot),
            new LayoutGridBounds(index, 0, 1, 1),
            new LayoutMinimumSize(120, 80))).ToArray());

    private static ScreenDefinition Screen(
        string id,
        LayoutDefinition layout,
        ConnectionId connectionId) => new(
        new ScreenId(id),
        ScreenDefinition.CurrentSchemaVersion,
        "Deploy",
        null,
        layout.Id,
        [TerminalPanel("screen-terminal", layout.Slots[0].Id.Value, connectionId)]);

    private static ScreenPanelDefinition TerminalPanel(
        string id,
        string slot,
        ConnectionId connectionId,
        string? location = null,
        params string[] commands) => new(
        new ScreenPanelId(id),
        new LayoutSlotId(slot),
        ScreenPanelKind.Terminal,
        "Terminal",
        connectionId,
        new PanelStartupBehavior(location, commands));
}
