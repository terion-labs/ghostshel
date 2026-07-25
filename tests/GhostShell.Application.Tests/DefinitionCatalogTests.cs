using GhostShell.Core;

namespace GhostShell.Application.Tests;

public sealed class DefinitionCatalogTests
{
    [Fact]
    public async Task Initialize_empty_catalog_seeds_a_bootable_local_workspace()
    {
        var fixture = new CatalogFixture();

        var result = await fixture.Catalog.InitializeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        var snapshot = Assert.IsType<DefinitionCatalogSnapshot>(result.Value);
        var connection = Assert.Single(snapshot.Connections).Value;
        Assert.Equal("builtin.local", connection.Id.Value);
        Assert.Equal("Local terminal", connection.Name);
        Assert.IsType<ConnectionEndpoint.Local>(connection.Endpoint);
        Assert.IsType<ConnectionAuthentication.None>(connection.Authentication);

        var layout = Assert.Single(snapshot.Layouts).Value;
        var screen = Assert.Single(snapshot.Screens).Value;
        Assert.Equal(layout.Id, screen.LayoutId);
        var panel = Assert.Single(screen.Panels);
        Assert.Equal(connection.Id, panel.ConnectionId);
        Assert.Equal(ScreenPanelKind.Terminal, panel.Kind);

        var workspace = Assert.Single(snapshot.Workspaces).Value;
        Assert.Contains(workspace.Entries, entry =>
            entry is WorkspaceEntry.ConnectionReference reference
            && reference.ConnectionId == connection.Id);
        Assert.Contains(workspace.Entries, entry =>
            entry is WorkspaceEntry.ScreenReference reference
            && reference.ScreenId == screen.Id);

        Assert.Equal(ThemePreference.Default, Assert.Single(snapshot.Themes).Value);
        var terminalProfile = Assert.Single(snapshot.TerminalProfiles).Value;
        Assert.Contains(snapshot.Keymaps, item => item.Value.Id == terminalProfile.KeymapId);
        Assert.Equal(
            BuiltInKeymaps.All.Select(item => item.Id).OrderBy(item => item.Value),
            snapshot.Keymaps.Select(item => item.Value.Id).OrderBy(item => item.Value));
        Assert.Equal(QuickTerminalSettings.Default, Assert.Single(snapshot.QuickTerminalSettings).Value);
    }

    [Fact]
    public async Task Initialize_loads_persisted_definitions_and_is_idempotent()
    {
        var fixture = new CatalogFixture();
        var persisted = CreateConnection("persisted", "Persisted local");
        fixture.Connections.Add(persisted, revision: 7);

        var first = await fixture.Catalog.InitializeAsync(CancellationToken.None);
        var attemptsAfterFirstInitialization = fixture.TotalSaveAttempts;
        var second = await fixture.Catalog.InitializeAsync(CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        var stored = Assert.Single(second.Value!.Connections);
        Assert.Equal(persisted, stored.Value);
        Assert.Equal(7, stored.Revision);
        Assert.Empty(second.Value.Layouts);
        Assert.Empty(second.Value.Screens);
        Assert.Empty(second.Value.Workspaces);
        Assert.Equal(attemptsAfterFirstInitialization, fixture.TotalSaveAttempts);
    }

    [Fact]
    public async Task Reload_publishes_definitions_written_outside_the_catalog_and_notifies()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var imported = CreateConnection("imported", "Imported connection");
        fixture.Connections.Add(imported, revision: 12);
        var changedCount = 0;
        fixture.Catalog.Changed += (_, _) => changedCount++;

        var result = await fixture.Catalog.ReloadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var stored = Assert.Single(
            result.Value!.Connections,
            item => item.Value.Id == imported.Id);
        Assert.Equal(12, stored.Revision);
        Assert.Same(result.Value, fixture.Catalog.Snapshot);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public async Task Failed_reload_preserves_the_last_complete_snapshot_without_notifying()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var published = fixture.Catalog.Snapshot;
        var imported = CreateConnection("not-published", "Not published");
        fixture.Connections.Add(imported);
        fixture.Layouts.ListError = new(
            DefinitionStoreErrorCode.StorageUnavailable,
            "The layout store is unavailable.");
        var changedCount = 0;
        fixture.Catalog.Changed += (_, _) => changedCount++;

        var result = await fixture.Catalog.ReloadAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.Same(published, fixture.Catalog.Snapshot);
        Assert.DoesNotContain(
            fixture.Catalog.Snapshot.Connections,
            item => item.Value.Id == imported.Id);
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task Reload_before_initialize_does_not_skip_first_use_seeding()
    {
        var fixture = new CatalogFixture();
        fixture.Connections.Add(CreateConnection("persisted", "Persisted connection"));

        var reloaded = await fixture.Catalog.ReloadAsync(CancellationToken.None);
        var initialized = await fixture.Catalog.InitializeAsync(CancellationToken.None);

        Assert.True(reloaded.IsSuccess, reloaded.Error?.Message);
        Assert.Empty(reloaded.Value!.Keymaps);
        Assert.True(initialized.IsSuccess, initialized.Error?.Message);
        Assert.Equal(BuiltInKeymaps.All.Count(), initialized.Value!.Keymaps.Count);
        Assert.Single(initialized.Value.Themes);
        Assert.Single(initialized.Value.TerminalProfiles);
        Assert.Single(initialized.Value.QuickTerminalSettings);
    }

    [Fact]
    public async Task Reload_is_serialized_with_catalog_mutations()
    {
        var connections = new PausingConnectionRepository();
        var catalog = new DefinitionCatalog(
            connections,
            new InMemoryDefinitionRepository<LayoutDefinition>(),
            new InMemoryDefinitionRepository<ScreenDefinition>(),
            new InMemoryDefinitionRepository<WorkspaceDefinition>(),
            new InMemoryDefinitionRepository<ThemePreference>(),
            new InMemoryDefinitionRepository<TerminalProfile>(),
            new InMemoryDefinitionRepository<KeymapProfile>(),
            new InMemoryDefinitionRepository<FileProviderProfile>(),
            new InMemoryDefinitionRepository<AiProviderProfile>(),
            new InMemoryDefinitionRepository<McpServerProfile>(),
            new InMemoryDefinitionRepository<QuickTerminalSettings>());
        Assert.True((await catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        connections.PauseNextList();
        var reload = catalog.ReloadAsync(CancellationToken.None).AsTask();
        await connections.ListPaused.WaitAsync(TimeSpan.FromSeconds(5));
        var saveAttemptsBefore = connections.SaveAttempts;
        using var cancellation = new CancellationTokenSource();
        var save = catalog.SaveConnectionAsync(
                CreateConnection("waiting-save", "Waiting save"),
                expectedRevision: null,
                cancellation.Token)
            .AsTask();

        try
        {
            Assert.False(save.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            Assert.Equal(saveAttemptsBefore, connections.SaveAttempts);
        }
        finally
        {
            connections.ResumeList();
        }

        Assert.True((await reload).IsSuccess);
    }

    [Fact]
    public async Task Save_refreshes_the_snapshot_and_notifies_after_persistence()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var changedCount = 0;
        fixture.Catalog.Changed += (_, _) => changedCount++;
        var connection = CreateConnection("staging", "Staging");

        var inserted = await fixture.Catalog.SaveConnectionAsync(
            connection,
            expectedRevision: null,
            CancellationToken.None);
        Assert.True(inserted.IsSuccess);

        var renamed = CreateConnection(connection.Id.Value, "Staging cluster");
        var updated = await fixture.Catalog.SaveConnectionAsync(
            renamed,
            expectedRevision: inserted.Value!.Revision,
            CancellationToken.None);

        Assert.True(updated.IsSuccess);
        Assert.Equal(2, updated.Value!.Revision);
        var snapshotItem = Assert.Single(
            fixture.Catalog.Snapshot.Connections,
            item => item.Value.Id == connection.Id);
        Assert.Equal("Staging cluster", snapshotItem.Value.Name);
        Assert.Equal(2, snapshotItem.Revision);
        var persisted = await fixture.Connections.GetAsync(connection.Key, CancellationToken.None);
        Assert.Equal(snapshotItem, persisted.Value);
        Assert.Equal(2, changedCount);
    }

    [Fact]
    public async Task Quick_terminal_settings_save_refreshes_the_durable_runtime_profile()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var current = Assert.Single(fixture.Catalog.Snapshot.QuickTerminalSettings);
        var updated = new QuickTerminalSettings(
            current.Value.Id,
            current.Value.Name,
            new KeyStroke("GRAVE", KeyModifiers.Control),
            QuickTerminalMonitorPolicy.Primary,
            0.42,
            0.75,
            8,
            animateSlide: false,
            animationDurationMilliseconds: 0,
            reduceMotion: true,
            restoreLastSession: false,
            hideOnFocusLoss: false);

        var saved = await fixture.Catalog.SaveQuickTerminalSettingsAsync(
            updated,
            current.Revision,
            CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        var refreshed = Assert.Single(fixture.Catalog.Snapshot.QuickTerminalSettings);
        Assert.Equal(updated, refreshed.Value);
        Assert.Equal(current.Revision + 1, refreshed.Revision);
    }

    [Fact]
    public async Task Save_rejects_duplicate_names_before_calling_the_repository()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var attemptsBeforeSave = fixture.Connections.SaveAttempts;
        var duplicate = CreateConnection("another-local", "local TERMINAL");

        var result = await fixture.Catalog.SaveConnectionAsync(
            duplicate,
            expectedRevision: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, result.Error!.Code);
        Assert.Equal(attemptsBeforeSave, fixture.Connections.SaveAttempts);
        Assert.DoesNotContain(
            fixture.Catalog.Snapshot.Connections,
            item => item.Value.Id == duplicate.Id);
    }

    [Fact]
    public async Task Save_screen_rejects_missing_layout_and_connection_dependencies()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var attemptsBeforeSave = fixture.Screens.SaveAttempts;
        var missingLayout = CreateScreen(
            "missing-layout",
            "Missing layout",
            new LayoutId("removed-layout"),
            connectionId: null);
        var defaultLayout = Assert.Single(fixture.Catalog.Snapshot.Layouts).Value;
        var missingConnection = CreateScreen(
            "missing-connection",
            "Missing connection",
            defaultLayout.Id,
            new ConnectionId("removed-connection"));

        var missingLayoutResult = await fixture.Catalog.SaveScreenAsync(
            missingLayout,
            expectedRevision: null,
            CancellationToken.None);
        var missingConnectionResult = await fixture.Catalog.SaveScreenAsync(
            missingConnection,
            expectedRevision: null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, missingLayoutResult.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, missingConnectionResult.Error!.Code);
        Assert.Equal(attemptsBeforeSave, fixture.Screens.SaveAttempts);
    }

    [Fact]
    public async Task Save_layout_rejects_a_change_that_would_invalidate_a_saved_screen()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var current = Assert.Single(fixture.Catalog.Snapshot.Layouts);
        var changedShape = new LayoutDefinition(
            current.Value.Id,
            LayoutDefinition.CurrentSchemaVersion,
            current.Value.Name,
            new LayoutGrid(2, 1),
            [
                CreateSlot("main", column: 0),
                CreateSlot("new-slot", column: 1),
            ]);

        var result = await fixture.Catalog.SaveLayoutAsync(
            changedShape,
            current.Revision,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, result.Error!.Code);
        Assert.Equal(current, Assert.Single(fixture.Catalog.Snapshot.Layouts));
    }

    [Fact]
    public async Task Save_workspace_rejects_a_missing_saved_screen_dependency()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("operations"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            null,
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("missing-screen"),
                    new ScreenId("removed-screen")),
            ]);

        var result = await fixture.Catalog.SaveWorkspaceAsync(
            workspace,
            expectedRevision: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, result.Error!.Code);
        Assert.DoesNotContain(
            fixture.Catalog.Snapshot.Workspaces,
            item => item.Value.Id == workspace.Id);
    }

    [Fact]
    public async Task Initialize_propagates_a_repository_error_without_publishing_a_partial_snapshot()
    {
        var fixture = new CatalogFixture();
        fixture.Layouts.ListError = new(
            DefinitionStoreErrorCode.StorageUnavailable,
            "The profile database is unavailable.");

        var result = await fixture.Catalog.InitializeAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.Equal(DefinitionCatalogSnapshot.Empty, fixture.Catalog.Snapshot);
        Assert.Equal(0, fixture.TotalSaveAttempts);
    }

    [Fact]
    public async Task Failed_save_is_propagated_without_changing_the_published_snapshot()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var originalSnapshot = fixture.Catalog.Snapshot;
        var changedCount = 0;
        fixture.Catalog.Changed += (_, _) => changedCount++;
        fixture.Connections.SaveError = new(
            DefinitionStoreErrorCode.StorageFailure,
            "The profile database rejected the write.");

        var result = await fixture.Catalog.SaveConnectionAsync(
            CreateConnection("unwritten", "Unwritten"),
            expectedRevision: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.StorageFailure, result.Error!.Code);
        Assert.Same(originalSnapshot, fixture.Catalog.Snapshot);
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task FileProviderProfilesPersistAndSftpRequiresAnSshConnection()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var localConnection = Assert.Single(fixture.Catalog.Snapshot.Connections).Value;
        var invalid = new FileProviderProfile(
            new FileProviderProfileId("files.invalid-sftp"),
            FileProviderProfile.CurrentSchemaVersion,
            "Invalid SFTP",
            new FileProviderConfiguration.Sftp(localConnection.Id));

        var rejected = await fixture.Catalog.SaveFileProviderProfileAsync(
            invalid,
            expectedRevision: null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, rejected.Error!.Code);
        Assert.Equal(0, fixture.FileProviderProfiles.SaveAttempts);

        var ssh = new ConnectionProfile(
            new ConnectionId("ssh-files"),
            ConnectionProfile.CurrentSchemaVersion,
            "SSH files",
            new ConnectionEndpoint.Ssh("files.example.test", username: "operator"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(30)),
            SshHostKeyPolicy.Strict);
        Assert.True((await fixture.Catalog.SaveConnectionAsync(
            ssh,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        var valid = new FileProviderProfile(
            new FileProviderProfileId("files.sftp"),
            FileProviderProfile.CurrentSchemaVersion,
            "SFTP files",
            new FileProviderConfiguration.Sftp(ssh.Id));

        var saved = await fixture.Catalog.SaveFileProviderProfileAsync(
            valid,
            expectedRevision: null,
            CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(valid, Assert.Single(fixture.Catalog.Snapshot.FileProviderProfiles).Value);
    }

    [Fact]
    public async Task SavedFilePanelRejectsAMissingProviderProfile()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var layout = Assert.Single(fixture.Catalog.Snapshot.Layouts).Value;
        var screen = new ScreenDefinition(
            new ScreenId("files-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Files",
            description: null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("files"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.FileViewer,
                    "Files",
                    ConnectionId: null,
                    PanelStartupBehavior.None,
                    new FileProviderProfileId("missing-provider")),
            ]);

        var result = await fixture.Catalog.SaveScreenAsync(
            screen,
            expectedRevision: null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, result.Error!.Code);
        Assert.Empty(fixture.Catalog.Snapshot.FileProviderProfiles);
    }

    [Fact]
    public async Task SavedFilePanelAcceptsTheIntrinsicHomeProviderWithoutPersistingAProfile()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var layout = Assert.Single(fixture.Catalog.Snapshot.Layouts).Value;
        var screen = new ScreenDefinition(
            new ScreenId("home-files-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Home files",
            description: null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("files"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.FileViewer,
                    "Files",
                    ConnectionId: null,
                    PanelStartupBehavior.None,
                    new FileProviderProfileId("builtin.files.home")),
            ]);

        var result = await fixture.Catalog.SaveScreenAsync(
            screen,
            expectedRevision: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            screen,
            Assert.Single(
                fixture.Catalog.Snapshot.Screens,
                item => item.Value.Id == screen.Id).Value);
        Assert.Empty(fixture.Catalog.Snapshot.FileProviderProfiles);
    }

    [Fact]
    public async Task WorkspaceTabAcceptsTheIntrinsicHomeProviderWithoutPersistingAProfile()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var layout = Assert.Single(fixture.Catalog.Snapshot.Layouts).Value;
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("home-files-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Home files",
            description: null,
            accent: null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("files-tab"),
                    "Files",
                    layout.Id,
                    [
                        new ScreenPanelDefinition(
                            new ScreenPanelId("files"),
                            new LayoutSlotId("main"),
                            ScreenPanelKind.FileViewer,
                            "Files",
                            ConnectionId: null,
                            PanelStartupBehavior.None,
                            new FileProviderProfileId("builtin.files.home")),
                    ]),
            ]);

        var result = await fixture.Catalog.SaveWorkspaceAsync(
            workspace,
            expectedRevision: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            workspace,
            Assert.Single(
                fixture.Catalog.Snapshot.Workspaces,
                item => item.Value.Id == workspace.Id).Value);
        Assert.Empty(fixture.Catalog.Snapshot.FileProviderProfiles);
    }

    [Fact]
    public async Task AiProviderProfilesPersistInDistinctFallbackOrder()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var primary = CreateAiProvider("ai.primary", "Primary", order: 0);
        var fallback = CreateAiProvider("ai.fallback", "Fallback", order: 0);

        var saved = await fixture.Catalog.SaveAiProviderProfileAsync(
            primary,
            expectedRevision: null,
            CancellationToken.None);
        var rejected = await fixture.Catalog.SaveAiProviderProfileAsync(
            fallback,
            expectedRevision: null,
            CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, rejected.Error!.Code);
        Assert.Equal(1, fixture.AiProviderProfiles.SaveAttempts);
        Assert.Equal(primary, Assert.Single(fixture.Catalog.Snapshot.AiProviderProfiles).Value);
    }

    [Fact]
    public async Task AgentPolicyProviderReferencesRequireAnExactEnabledProfile()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var layout = Assert.Single(fixture.Catalog.Snapshot.Layouts).Value;
        var missingPolicy = AgentPolicyFor(new AiProviderProfileId("ai.missing"));
        var missingScreen = CreatePolicyScreen(
            "missing-ai-policy-screen",
            "Missing AI policy screen",
            layout.Id,
            missingPolicy);
        var missingWorkspace = new WorkspaceDefinition(
            new WorkspaceId("missing-ai-policy-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Missing AI policy workspace",
            description: null,
            accent: null,
            entries: [],
            agentPolicyOverride: missingPolicy);

        var missingScreenResult = await fixture.Catalog.SaveScreenAsync(
            missingScreen,
            expectedRevision: null,
            CancellationToken.None);
        var missingWorkspaceResult = await fixture.Catalog.SaveWorkspaceAsync(
            missingWorkspace,
            expectedRevision: null,
            CancellationToken.None);

        Assert.Equal(
            DefinitionStoreErrorCode.DependencyConflict,
            missingScreenResult.Error!.Code);
        Assert.Equal(
            DefinitionStoreErrorCode.DependencyConflict,
            missingWorkspaceResult.Error!.Code);

        var disabled = CreateAiProvider(
            "ai.disabled",
            "Disabled",
            order: 0,
            isEnabled: false);
        Assert.True((await fixture.Catalog.SaveAiProviderProfileAsync(
            disabled,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        var disabledScreen = CreatePolicyScreen(
            "disabled-ai-policy-screen",
            "Disabled AI policy screen",
            layout.Id,
            AgentPolicyFor(disabled.Id));

        var disabledResult = await fixture.Catalog.SaveScreenAsync(
            disabledScreen,
            expectedRevision: null,
            CancellationToken.None);

        Assert.Equal(
            DefinitionStoreErrorCode.DependencyConflict,
            disabledResult.Error!.Code);
    }

    [Fact]
    public async Task AiProviderDeletionIsBlockedByScreenAndWorkspacePolicies()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var layout = Assert.Single(fixture.Catalog.Snapshot.Layouts).Value;
        var provider = CreateAiProvider("ai.policy", "Policy provider", order: 0);
        var storedProvider = await fixture.Catalog.SaveAiProviderProfileAsync(
            provider,
            expectedRevision: null,
            CancellationToken.None);
        Assert.True(storedProvider.IsSuccess, storedProvider.Error?.Message);
        var policy = AgentPolicyFor(provider.Id);
        var screen = CreatePolicyScreen(
            "ai-policy-screen",
            "AI policy screen",
            layout.Id,
            policy);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("ai-policy-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "AI policy workspace",
            description: null,
            accent: null,
            entries: [],
            agentPolicyOverride: policy);
        Assert.True((await fixture.Catalog.SaveScreenAsync(
            screen,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await fixture.Catalog.SaveWorkspaceAsync(
            workspace,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        var disabledProvider = CreateAiProvider(
            provider.Id.Value,
            provider.Name,
            provider.Order,
            isEnabled: false);
        var saveAttempts = fixture.AiProviderProfiles.SaveAttempts;

        var disabled = await fixture.Catalog.SaveAiProviderProfileAsync(
            disabledProvider,
            storedProvider.Value!.Revision,
            CancellationToken.None);

        Assert.False(disabled.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, disabled.Error!.Code);
        Assert.Equal(saveAttempts, fixture.AiProviderProfiles.SaveAttempts);
        var deleteAttempts = fixture.AiProviderProfiles.DeleteAttempts;

        var deleted = await fixture.Catalog.DeleteAsync(
            provider.Key,
            storedProvider.Value!.Revision,
            CancellationToken.None);

        Assert.False(deleted.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, deleted.Error!.Code);
        Assert.Equal(deleteAttempts, fixture.AiProviderProfiles.DeleteAttempts);
        Assert.Contains(
            fixture.Catalog.Snapshot.AiProviderProfiles,
            item => item.Value.Id == provider.Id);
    }

    [Fact]
    public async Task McpServerProfilesPersistRejectDuplicateNamesAndDeleteThroughTheCatalog()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var profile = CreateMcpServer("mcp.primary", "Primary MCP server");
        var duplicateName = CreateMcpServer("mcp.other", "primary mcp SERVER");

        var saved = await fixture.Catalog.SaveMcpServerProfileAsync(
            profile,
            expectedRevision: null,
            CancellationToken.None);
        var rejected = await fixture.Catalog.SaveMcpServerProfileAsync(
            duplicateName,
            expectedRevision: null,
            CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, rejected.Error!.Code);
        Assert.Equal(1, fixture.McpServerProfiles.SaveAttempts);
        Assert.Equal(
            profile,
            Assert.Single(fixture.Catalog.Snapshot.McpServerProfiles).Value);

        var deleted = await fixture.Catalog.DeleteAsync(
            profile.Key,
            saved.Value!.Revision,
            CancellationToken.None);

        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Empty(fixture.Catalog.Snapshot.McpServerProfiles);
        Assert.Equal(1, fixture.McpServerProfiles.DeleteAttempts);
    }

    [Fact]
    public async Task Delete_rejects_an_unknown_definition_kind_without_touching_repositories()
    {
        var fixture = new CatalogFixture();
        Assert.True((await fixture.Catalog.InitializeAsync(CancellationToken.None)).IsSuccess);

        var result = await fixture.Catalog.DeleteAsync(
            new DefinitionKey(new DefinitionKind("future-kind"), "future-id"),
            expectedRevision: 1,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.UnsupportedKind, result.Error!.Code);
        Assert.Equal(0, fixture.TotalDeleteAttempts);
    }

    private static ConnectionProfile CreateConnection(string id, string name) =>
        new(
            new ConnectionId(id),
            ConnectionProfile.CurrentSchemaVersion,
            name,
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);

    private static AiProviderProfile CreateAiProvider(
        string id,
        string name,
        int order,
        bool isEnabled = true) =>
        new(
            new AiProviderProfileId(id),
            AiProviderProfile.CurrentSchemaVersion,
            name,
            AiProviderKind.OpenAiCompatible,
            new Uri("http://localhost:11434/v1/"),
            new AiProviderAuthentication.None(),
            "local-model",
            order,
            isEnabled);

    private static McpServerProfile CreateMcpServer(string id, string name) =>
        new(
            new McpServerProfileId(id),
            McpServerProfile.CurrentSchemaVersion,
            name,
            "/usr/local/bin/mcp-server",
            ["--stdio"],
            workingDirectory: null,
            environment: [],
            enabledTools: ["status.read"]);

    private static AgentPolicy AgentPolicyFor(AiProviderProfileId profileId) =>
        AgentPolicy.Default with
        {
            Provider = profileId.Value,
            Model = "policy-model",
        };

    private static ScreenDefinition CreatePolicyScreen(
        string id,
        string name,
        LayoutId layoutId,
        AgentPolicy policy)
    {
        var screen = CreateScreen(id, name, layoutId, connectionId: null);
        return new ScreenDefinition(
            screen.Id,
            screen.SchemaVersion,
            screen.Name,
            screen.Description,
            screen.LayoutId,
            screen.Panels,
            screen.Tags,
            policy);
    }

    private static ScreenDefinition CreateScreen(
        string id,
        string name,
        LayoutId layoutId,
        ConnectionId? connectionId) =>
        new(
            new ScreenId(id),
            ScreenDefinition.CurrentSchemaVersion,
            name,
            null,
            layoutId,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    null,
                    connectionId,
                    PanelStartupBehavior.None),
            ]);

    private static LayoutSlotDefinition CreateSlot(string id, int column) =>
        new(
            new LayoutSlotId(id),
            new LayoutGridBounds(column, 0, 1, 1),
            new LayoutMinimumSize(320, 180));

    private sealed class CatalogFixture
    {
        public CatalogFixture()
        {
            Catalog = new DefinitionCatalog(
                Connections,
                Layouts,
                Screens,
                Workspaces,
                Themes,
                TerminalProfiles,
                Keymaps,
                FileProviderProfiles,
                AiProviderProfiles,
                McpServerProfiles,
                QuickTerminalSettings);
        }

        public InMemoryDefinitionRepository<ConnectionProfile> Connections { get; } = new();

        public InMemoryDefinitionRepository<LayoutDefinition> Layouts { get; } = new();

        public InMemoryDefinitionRepository<ScreenDefinition> Screens { get; } = new();

        public InMemoryDefinitionRepository<WorkspaceDefinition> Workspaces { get; } = new();

        public InMemoryDefinitionRepository<ThemePreference> Themes { get; } = new();

        public InMemoryDefinitionRepository<TerminalProfile> TerminalProfiles { get; } = new();

        public InMemoryDefinitionRepository<KeymapProfile> Keymaps { get; } = new();

        public InMemoryDefinitionRepository<FileProviderProfile> FileProviderProfiles { get; } = new();

        public InMemoryDefinitionRepository<AiProviderProfile> AiProviderProfiles { get; } = new();

        public InMemoryDefinitionRepository<McpServerProfile> McpServerProfiles { get; } = new();

        public InMemoryDefinitionRepository<QuickTerminalSettings> QuickTerminalSettings { get; } = new();

        public DefinitionCatalog Catalog { get; }

        public int TotalSaveAttempts =>
            Connections.SaveAttempts
            + Layouts.SaveAttempts
            + Screens.SaveAttempts
            + Workspaces.SaveAttempts
            + Themes.SaveAttempts
            + TerminalProfiles.SaveAttempts
            + Keymaps.SaveAttempts
            + FileProviderProfiles.SaveAttempts
            + AiProviderProfiles.SaveAttempts
            + McpServerProfiles.SaveAttempts
            + QuickTerminalSettings.SaveAttempts;

        public int TotalDeleteAttempts =>
            Connections.DeleteAttempts
            + Layouts.DeleteAttempts
            + Screens.DeleteAttempts
            + Workspaces.DeleteAttempts
            + Themes.DeleteAttempts
            + TerminalProfiles.DeleteAttempts
            + Keymaps.DeleteAttempts
            + FileProviderProfiles.DeleteAttempts
            + AiProviderProfiles.DeleteAttempts
            + McpServerProfiles.DeleteAttempts
            + QuickTerminalSettings.DeleteAttempts;
    }

    private sealed class PausingConnectionRepository
        : IDefinitionRepository<ConnectionProfile>
    {
        private readonly InMemoryDefinitionRepository<ConnectionProfile> _inner = new();
        private readonly SemaphoreSlim _resumeList = new(0, 1);
        private TaskCompletionSource _listPaused = CreateSignal();
        private int _pauseNextList;

        public int SaveAttempts => _inner.SaveAttempts;

        public Task ListPaused => _listPaused.Task;

        public void PauseNextList()
        {
            _listPaused = CreateSignal();
            if (Interlocked.Exchange(ref _pauseNextList, 1) != 0)
            {
                throw new InvalidOperationException("A list operation is already paused.");
            }
        }

        public void ResumeList() => _resumeList.Release();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> GetAsync(
            DefinitionKey key,
            CancellationToken cancellationToken) => _inner.GetAsync(key, cancellationToken);

        public async ValueTask<DefinitionStoreResult<IReadOnlyList<StoredDefinition<ConnectionProfile>>>>
            ListAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _pauseNextList, 0) == 1)
            {
                _listPaused.TrySetResult();
                await _resumeList.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await _inner.ListAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveAsync(
            ConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
            _inner.SaveAsync(definition, expectedRevision, cancellationToken);

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(key, expectedRevision, cancellationToken);

        private static TaskCompletionSource CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
