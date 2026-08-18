namespace GhostShell.Core.Tests;

public sealed class WorkspaceDefinitionTests
{
    [Fact]
    public void Moving_an_entry_changes_order_without_changing_identity()
    {
        var workspace = CreateWorkspace(
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("connection-entry"),
                new ConnectionId("production")),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("screen-entry"),
                new ScreenId("deploy")),
            CreateTabEntry("notes-entry"));

        var reordered = workspace.MoveEntry(new WorkspaceEntryId("screen-entry"), 0);

        Assert.Equal(
            ["screen-entry", "connection-entry", "notes-entry"],
            reordered.Entries.Select(entry => entry.Id.Value), StringComparer.Ordinal);
        Assert.Equal(workspace.Id, reordered.Id);
        Assert.Equal(workspace.Icon, reordered.Icon);
    }

    [Fact]
    public void Validator_rejects_duplicate_entry_ids()
    {
        var workspace = CreateWorkspace(
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("duplicate"),
                new ConnectionId("production")),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("duplicate"),
                new ScreenId("deploy")));

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(result.Issues, issue => issue.Code == DefinitionValidationCode.DuplicateId);
    }

    [Fact]
    public void Workspace_models_connection_screen_and_workspace_only_tab_entries()
    {
        var workspace = CreateWorkspace(
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId("connection"),
                new ConnectionId("production")),
            new WorkspaceEntry.ScreenReference(
                new WorkspaceEntryId("screen"),
                new ScreenId("deploy")),
            CreateTabEntry("tab"));

        var result = WorkspaceValidator.Validate(workspace);

        Assert.True(result.IsValid);
        Assert.IsType<WorkspaceEntry.ConnectionReference>(workspace.Entries[0]);
        Assert.IsType<WorkspaceEntry.ScreenReference>(workspace.Entries[1]);
        Assert.IsType<WorkspaceEntry.Tab>(workspace.Entries[2]);
    }

    [Fact]
    public void Validator_rejects_non_semantic_icon_identifiers()
    {
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            "#FF8400",
            [],
            icon: "Not an icon!");

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidEntry
                && issue.Message.Contains("icon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_run_local_yolo_as_a_saved_policy_override()
    {
        var yoloPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("unsafe-policy"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Unsafe policy",
            null,
            null,
            [],
            agentPolicyOverride: yoloPolicy);

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidAgentPolicy
                && issue.Message.Contains("YOLO", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_non_default_delivery_failure_policy_on_non_terminal_tab_panel()
    {
        var browser = new ScreenPanelDefinition(
            new ScreenPanelId("browser"),
            new LayoutSlotId("main"),
            ScreenPanelKind.Browser,
            "Browser",
            null,
            new PanelStartupBehavior(
                "https://example.test",
                deliveryFailurePolicy:
                    StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure));
        var workspace = CreateWorkspace(
            new WorkspaceEntry.Tab(
                new WorkspaceEntryId("browser-tab"),
                "Browser",
                new LayoutId("single"),
                [browser]));

        var result = WorkspaceValidator.Validate(workspace);

        Assert.Contains(
            result.Issues,
            issue => issue.Code == DefinitionValidationCode.InvalidPanel
                && string.Equals(issue.Target, browser.Id.Value
, StringComparison.Ordinal) && issue.Message.Contains(
                    "delivery failure policy",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static WorkspaceDefinition CreateWorkspace(params WorkspaceEntry[] entries) =>
        new(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            "#FF8400",
            entries);

    private static WorkspaceEntry.Tab CreateTabEntry(string id) =>
        new(
            new WorkspaceEntryId(id),
            "Scratch",
            new LayoutId("single"),
            [
                new(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    "Terminal",
                    null,
                    PanelStartupBehavior.None),
            ]);

    [Fact]
    public void A_workspace_saved_before_colours_existed_still_deserializes()
    {
        // Definitions persist as JSON, so an older payload simply lacks the
        // property. Built by serializing a current definition and removing
        // the colour, so the fixture cannot drift from the real shape.
        var current = new WorkspaceDefinition(
            new WorkspaceId("workspace-old"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Older",
            null,
            accent: null,
            [],
            icon: "workspace");
        var older = System.Text.Json.JsonSerializer.Serialize(current)
            .Replace(",\"Color\":null", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Color", older, StringComparison.Ordinal);

        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceDefinition>(older);

        Assert.NotNull(restored);
        Assert.Null(restored!.Color);
        Assert.Null(restored.Accent);
        Assert.Equal("workspace", restored.Icon);
    }

    [Fact]
    public void Colour_and_accent_are_independent()
    {
        // The tile colour identifies the workspace; the accent retints the
        // shell. One must never imply the other, or unsetting the accent
        // would silently repaint the rail.
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("workspace-appearance"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Production",
            null,
            accent: null,
            [],
            icon: "rocket",
            color: "  #B4543A  ");

        Assert.Equal("#B4543A", workspace.Color);
        Assert.Null(workspace.Accent);

        var round = System.Text.Json.JsonSerializer.Deserialize<WorkspaceDefinition>(
            System.Text.Json.JsonSerializer.Serialize(workspace));
        Assert.Equal("#B4543A", round!.Color);
        Assert.Null(round.Accent);
    }
}
