using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Controls;
using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docker;

namespace GhostShell.App.Tests;

public sealed class MainWindowRuntimeGraphIntegrationTests
{
    [Fact]
    public async Task EnablingGlobalMultiplexingAffectsAnAlreadyOpenInheritedWorkspace()
    {
        var snapshot = CreateCatalogSnapshot();
        var ssh = new ConnectionProfile(
            new ConnectionId("runtime-graph-ssh"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote",
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        snapshot = snapshot with
        {
            Connections = [.. snapshot.Connections, Store(ssh)],
        };
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, snapshot);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        Assert.True(await viewModel.SetUseTerminalMultiplexingForSshTerminalsAsync(true));
        Assert.True(await viewModel.AddConnectionPanelAsync(ssh.Id));

        var terminal = Assert.IsType<TerminalRuntimePanelViewModel>(viewModel.ActivePanel);
        Assert.Equal(ssh.Id, terminal.ConnectionId);
        Assert.NotNull(terminal.MultiplexerSession);
    }

    [Fact]
    public async Task Opening_workspace_registers_typed_ordered_graph_and_active_ids()
    {
        var snapshot = CreateCatalogSnapshot();
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, snapshot);

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        var registration = Assert.Single(recorder.Registrations);
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var graph = registration.Request.Workspace;

        Assert.Equal(viewModel.WindowId, registration.Request.WindowId);
        Assert.Null(registration.Context.ExpectedRevision);
        Assert.NotNull(registration.Context.IdempotencyKey);
        Assert.Equal(ActorKind.Human, registration.Context.Actor.Kind);
        Assert.Equal(runtime.Id, graph.Id);
        Assert.Equal("Runtime graph", graph.Title);
        Assert.Equal(["Alpha", "Beta", "Gamma"], graph.Tabs.Select(tab => tab.Title));
        Assert.Equal(
            [PanelKind.Terminal, PanelKind.Browser, PanelKind.FileViewer],
            graph.Tabs[0].Panels.Select(panel => panel.Kind));
        Assert.Equal([PanelKind.Statistics], graph.Tabs[1].Panels.Select(panel => panel.Kind));
        Assert.Equal([PanelKind.ProcessMonitor], graph.Tabs[2].Panels.Select(panel => panel.Kind));
        Assert.Equal(
            ["Terminal", "Browser", "Files"],
            graph.Tabs[0].Panels.Select(panel => panel.Title));
        Assert.Equal(runtime.Tabs.Select(tab => tab.Id), graph.Tabs.Select(tab => tab.Id));
        Assert.Equal(runtime.ActiveTab!.Id, graph.ActiveTabId);
        Assert.All(
            graph.Tabs.Zip(runtime.Tabs),
            pair => Assert.Equal(pair.Second.ActivePanelId, pair.First.ActivePanelId));
        Assert.All(
            graph.Tabs.SelectMany(tab => tab.Panels),
            panel => Assert.Null(panel.SessionId));
        Assert.Equal(1, runtime.HostRevision);
    }

    [Fact]
    public async Task Autosave_workspace_writes_live_tabs_back_as_a_batched_definition_save()
    {
        var snapshot = CreateCatalogSnapshot();
        var stored = snapshot.Workspaces.Single(item => item.Value.Id == WorkspaceId);
        var autosaveWorkspace = new WorkspaceDefinition(
            stored.Value.Id,
            stored.Value.SchemaVersion,
            stored.Value.Name,
            stored.Value.Description,
            stored.Value.Accent,
            stored.Value.Entries,
            stored.Value.AgentPolicyOverride,
            stored.Value.Icon,
            autoSave: true);
        snapshot = snapshot with
        {
            Workspaces = [Store(autosaveWorkspace)],
        };
        var (client, _) = CreateSessionClient();
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingAutoSaveCatalogProxy>();
        var proxy = (RecordingAutoSaveCatalogProxy)(object)catalog;
        proxy.Snapshot = snapshot;
        using var viewModel = CreateViewModel(client, catalog);

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        // The autosave debounce is 1.5 s; poll rather than assuming timing.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (proxy.SavedWorkspace is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var saved = Assert.IsType<WorkspaceDefinition>(proxy.SavedWorkspace);
        Assert.True(saved.AutoSave);
        Assert.Equal(stored.Revision, proxy.SavedWorkspaceRevision);
        var tabs = saved.Entries.Cast<WorkspaceEntry.Tab>().ToArray();
        Assert.Equal(["Alpha", "Beta", "Gamma"], tabs.Select(tab => tab.Name));
        // Stored tab entries are matched by name, so entry ids stay stable.
        Assert.Equal(["alpha", "beta", "gamma"], tabs.Select(tab => tab.Id.Value));
        Assert.Equal(
            [ScreenPanelKind.Terminal, ScreenPanelKind.Browser, ScreenPanelKind.FileViewer],
            tabs[0].Panels.Select(panel => panel.Kind));

        var layouts = proxy.SavedLayouts;
        Assert.NotNull(layouts);
        Assert.Equal(3, layouts.Count);
        Assert.All(layouts, item =>
        {
            Assert.True(LayoutDefinition.IsAutoSaved(item.Definition.Id));
            Assert.Null(item.ExpectedRevision);
            Assert.NotNull(item.Definition.DockLayoutJson);
        });
        foreach (var (tab, layout) in tabs.Zip(layouts.Select(item => item.Definition)))
        {
            Assert.Equal(layout.Id, tab.LayoutId);
            // Every captured layout slot is mapped by exactly one tab panel.
            Assert.Equal(
                layout.Slots.Select(slot => slot.Id.Value).Order(),
                tab.Panels.Select(panel => panel.SlotId.Value).Order());
        }
    }

    [Fact]
    public async Task Agent_panel_floats_hidden_by_default_and_the_pin_docks_and_persists()
    {
        var snapshot = CreateCatalogSnapshot();
        var stored = snapshot.Workspaces.Single(item => item.Value.Id == WorkspaceId);
        var (client, _) = CreateSessionClient();
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingAutoSaveCatalogProxy>();
        var proxy = (RecordingAutoSaveCatalogProxy)(object)catalog;
        proxy.Snapshot = snapshot;
        using var viewModel = CreateViewModel(client, catalog);

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        // Unpinned is the default: nothing on screen until asked for, and then
        // a flyout rather than a slot in the layout.
        Assert.False(viewModel.IsAgentPanelVisible);
        Assert.False(viewModel.IsAgentPanelDocked);
        Assert.False(viewModel.IsAgentPanelDockedVisible);

        viewModel.ToggleAgentPanel();
        Assert.True(viewModel.IsAgentPanelVisible);
        Assert.False(viewModel.IsAgentPanelDockedVisible);

        await viewModel.ToggleAgentPanelPinAsync(CancellationToken.None);

        Assert.True(viewModel.IsAgentPanelDocked);
        Assert.True(viewModel.IsAgentPanelDockedVisible);
        var saved = Assert.IsType<WorkspaceDefinition>(proxy.SavedWorkspace);
        Assert.True(saved.AgentPanelPinned);
        Assert.Equal(stored.Revision, proxy.SavedWorkspaceRevision);
        // The pin write must carry the rest of the definition, not reset it.
        Assert.Equal(stored.Value.Name, saved.Name);
        Assert.Equal(stored.Value.Entries.Count, saved.Entries.Count);
    }

    [Fact]
    public async Task Opening_a_workspace_with_a_pinned_agent_panel_shows_it_docked()
    {
        var snapshot = CreateCatalogSnapshot();
        var stored = snapshot.Workspaces.Single(item => item.Value.Id == WorkspaceId);
        var pinnedWorkspace = new WorkspaceDefinition(
            stored.Value.Id,
            stored.Value.SchemaVersion,
            stored.Value.Name,
            stored.Value.Description,
            stored.Value.Accent,
            stored.Value.Entries,
            stored.Value.AgentPolicyOverride,
            stored.Value.Icon,
            stored.Value.AutoSave,
            stored.Value.Color,
            agentPanelPinned: true);
        snapshot = snapshot with
        {
            Workspaces = snapshot.Workspaces
                .Select(item => item.Value.Id == WorkspaceId ? Store(pinnedWorkspace) : item)
                .ToArray(),
        };
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, snapshot);

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        Assert.True(viewModel.IsAgentPanelVisible);
        Assert.True(viewModel.IsAgentPanelDocked);
        Assert.True(viewModel.IsAgentPanelDockedVisible);
    }

    [Fact]
    public async Task Database_tab_appends_a_single_panel_tab()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var tabCount = runtime.Tabs.Count;

        // The New-tab catalog path; it used to reject DatabaseViewer at the
        // single-panel-tab kind gate and crash the dispatcher.
        Assert.True(await viewModel.AddDatabaseTabAsync());

        Assert.Equal(tabCount + 1, runtime.Tabs.Count);
        var tab = runtime.Tabs[^1];
        Assert.Equal("Database", tab.Title);
        var panel = Assert.Single(tab.Panels);
        Assert.Equal(PanelKind.DatabaseViewer, panel.Kind);
    }

    [Fact]
    public async Task Switching_terminal_connection_preserves_panel_identity_and_layout()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var original = Assert.IsType<TerminalRuntimePanelViewModel>(
            Assert.Single(
                tab.Panels,
                panel => panel.Kind == PanelKind.Terminal));
        await original.Initialization;
        var layout = (
            original.LayoutColumn,
            original.LayoutRow,
            original.LayoutColumnSpan,
            original.LayoutRowSpan,
            original.LayoutMinimumWidth,
            original.LayoutMinimumHeight);

        Assert.True(viewModel.ReplaceTerminalConnection(
            original,
            AppendedConnectionId));

        var replacement = Assert.IsType<TerminalRuntimePanelViewModel>(
            Assert.Single(
                tab.Panels,
                panel => panel.Kind == PanelKind.Terminal));
        await replacement.Initialization;
        Assert.NotSame(original, replacement);
        Assert.Equal(original.Id, replacement.Id);
        Assert.Equal(original.Title, replacement.Title);
        Assert.Equal(AppendedConnectionId, replacement.ConnectionId);
        Assert.Equal("Local", replacement.ConnectionDisplayName);
        Assert.Equal(
            layout,
            (
                replacement.LayoutColumn,
                replacement.LayoutRow,
                replacement.LayoutColumnSpan,
                replacement.LayoutRowSpan,
                replacement.LayoutMinimumWidth,
                replacement.LayoutMinimumHeight));
        Assert.Null(original.SessionRequest);
        Assert.Same(replacement, tab.ActivePanel);
        Assert.Contains(
            runtime.Connections,
            connection => connection.Id == AppendedConnectionId);

        // A close notification can replace or retire the original view-model before
        // the selector continuation runs. The stable panel ID must still identify the
        // live layout slot instead of leaving the just-closed panel behind.
        Assert.True(viewModel.ReplaceTerminalConnection(
            original,
            original.ConnectionId));
        var switchedAgain = Assert.IsType<TerminalRuntimePanelViewModel>(
            Assert.Single(
                tab.Panels,
                panel => panel.Kind == PanelKind.Terminal));
        await switchedAgain.Initialization;
        Assert.Equal(original.Id, switchedAgain.Id);
        Assert.Equal(original.Title, switchedAgain.Title);
        Assert.Equal(original.ConnectionId, switchedAgain.ConnectionId);
        Assert.Single(recorder.Registrations);
    }

    [Fact]
    public void File_connection_options_unify_live_and_unavailable_provider_targets()
    {
        var ftpId = new FileProviderProfileId("files.ftp.production");
        var unavailableId = new FileProviderProfileId("files.webdav.unavailable");
        var ftp = new FileProviderProfile(
            ftpId,
            FileProviderProfile.CurrentSchemaVersion,
            "Production FTP",
            new FileProviderConfiguration.Ftp(
                "files.example.test",
                21,
                null,
                null,
                FtpSecurityMode.ExplicitTls,
                FtpConnectionMode.AutoPassive));
        var unavailable = new FileProviderProfile(
            unavailableId,
            FileProviderProfile.CurrentSchemaVersion,
            "Unavailable WebDAV",
            new FileProviderConfiguration.WebDav(
                new Uri("https://dav.example.test/"),
                null,
                null,
                false));
        var snapshot = CreateCatalogSnapshot() with
        {
            FileProviderProfiles = [Store(ftp), Store(unavailable)],
        };
        var transientSftpId = new FileProviderProfileId(
            "builtin.files.connection.runtime-graph-ssh");
        var files = new EmptyFileClients(
            [
                FileProfile(
                    BuiltInFileProviders.HomeId,
                    "Local",
                    FileProviderFamily.Posix,
                    "local"),
                FileProfile(
                    ftpId,
                    ftp.Name,
                    FileProviderFamily.Ftp,
                    "files.example.test"),
                FileProfile(
                    transientSftpId,
                    "Production SSH",
                    FileProviderFamily.Sftp,
                    "ssh.example.test"),
            ]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            snapshot,
            filePanelClient: files,
            fileTransferQueueClient: files);

        var options = viewModel.FileConnectionOptions;

        Assert.Collection(
            options,
            option =>
            {
                Assert.Equal("Local", option.Name);
                Assert.Equal("Local", option.Kind);
                Assert.True(option.CanOpen);
            },
            option =>
            {
                Assert.Equal("Production FTP", option.Name);
                Assert.Equal("FTP/FTPS", option.Kind);
                Assert.True(option.CanOpen);
            },
            option =>
            {
                Assert.Equal("Production SSH", option.Name);
                Assert.Equal("SFTP", option.Kind);
                Assert.True(option.CanOpen);
            },
            option =>
            {
                Assert.Equal("Unavailable WebDAV", option.Name);
                Assert.Equal("WebDAV", option.Kind);
                Assert.False(option.CanOpen);
            });
        Assert.All(
            options,
            option => Assert.IsType<PanelConnectionOptionViewModel.Target.FileProvider>(
                option.Selection));
    }

    [Fact]
    public async Task Active_command_context_matches_only_the_active_panel_kind()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);

        Assert.NotEqual(
            CommandContext.None,
            viewModel.ActiveCommandContexts & CommandContext.Terminal);
        Assert.Equal(
            CommandContext.None,
            viewModel.ActiveCommandContexts & CommandContext.Browser);

        var browser = Assert.Single(
            runtime.ActiveTab!.Panels,
            panel => panel.Kind == PanelKind.Browser);
        Assert.True(await viewModel.ActivatePanelAsync(browser.Id));
        Assert.Equal(
            CommandContext.None,
            viewModel.ActiveCommandContexts & CommandContext.Terminal);
        Assert.NotEqual(
            CommandContext.None,
            viewModel.ActiveCommandContexts & CommandContext.Browser);

        var files = Assert.Single(
            runtime.ActiveTab.Panels,
            panel => panel.Kind == PanelKind.FileViewer);
        Assert.True(await viewModel.ActivatePanelAsync(files.Id));
        Assert.Equal(
            CommandContext.None,
            viewModel.ActiveCommandContexts
            & (CommandContext.Terminal | CommandContext.Browser));

        Assert.True(await viewModel.ActivateTabAsync(runtime.Tabs[1].Id));
        Assert.Equal(PanelKind.Statistics, runtime.ActiveTab?.ActivePanel?.Kind);
        Assert.Equal(
            CommandContext.None,
            viewModel.ActiveCommandContexts
            & (CommandContext.Terminal | CommandContext.Browser));
    }

    [Fact]
    public async Task BrowserAdapterCreatesConcretePanelsForSavedAndNewRuntimeEntries()
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            browserRendererFactory: browserFactory);

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var savedBrowser = Assert.IsType<BrowserRuntimePanelViewModel>(
            Assert.Single(
                runtime.ActiveTab!.Panels,
                panel => panel.Kind == PanelKind.Browser));
        Assert.Equal(BrowserAddress.Blank, savedBrowser.CurrentAddress);
        Assert.Equal(1, browserFactory.CreateCount);
        Assert.True(viewModel.CanCreateBrowserPanel);

        Assert.True(await viewModel.AddBrowserPanelAsync());

        var addedBrowser = Assert.IsType<BrowserRuntimePanelViewModel>(
            runtime.ActiveTab.ActivePanel);
        Assert.NotEqual(savedBrowser.Id, addedBrowser.Id);
        Assert.Equal(2, browserFactory.CreateCount);
        var graph = Assert.IsType<WorkspaceGraphSnapshot>(
            recorder.CurrentWorkspace).Workspace;
        var activeTab = Assert.Single(
            graph.Tabs,
            tab => tab.Id == graph.ActiveTabId);
        Assert.Equal(
            PanelKind.Browser,
            Assert.Single(
                activeTab.Panels,
                panel => panel.Id == activeTab.ActivePanelId).Kind);
    }

    [Fact]
    public async Task Browser_selector_offers_local_and_ssh_routes_and_switch_preserves_address()
    {
        var ssh = new ConnectionProfile(
            new ConnectionId("browser-ssh"),
            ConnectionProfile.CurrentSchemaVersion,
            "Browser bastion",
            new ConnectionEndpoint.Ssh("bastion.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var docker = new ConnectionProfile(
            new ConnectionId("browser-docker"),
            ConnectionProfile.CurrentSchemaVersion,
            "Browser container",
            new ConnectionEndpoint.Docker("app"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var snapshot = CreateCatalogSnapshot();
        snapshot = snapshot with
        {
            Connections = snapshot.Connections
                .Append(Store(ssh))
                .Append(Store(docker))
                .ToArray(),
        };
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            snapshot,
            browserRendererFactory: browserFactory);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var current = Assert.IsType<BrowserRuntimePanelViewModel>(
            Assert.Single(viewModel.RuntimeWorkspace!.ActiveTab!.Panels,
                panel => panel.Kind == PanelKind.Browser));
        var address = new BrowserAddress(
            new Uri("https://internal.example.test/app"));
        current.ApplyBrowserState(new BrowserSessionState(
            address,
            "Internal",
            BrowserLoadState.Ready,
            canGoBack: false,
            canGoForward: false,
            documentRevision: 1));

        var routeIds = viewModel.BrowserConnectionOptions
            .Select(option => Assert.IsType<PanelConnectionOptionViewModel.Target.Connection>(
                option.Selection).Id)
            .ToHashSet();
        Assert.Equal(2, routeIds.Count);
        Assert.Contains(new ConnectionId("runtime-graph-local"), routeIds);
        Assert.Contains(ssh.Id, routeIds);
        Assert.DoesNotContain(docker.Id, routeIds);
        Assert.True(viewModel.ReplacePanelConnection(current, ssh));

        var replacement = Assert.IsType<BrowserRuntimePanelViewModel>(
            Assert.Single(viewModel.RuntimeWorkspace.ActiveTab.Panels,
                panel => panel.Id == current.Id));
        Assert.Equal(ssh.Id, replacement.ConnectionId);
        Assert.Equal(address, replacement.CurrentAddress);
        await replacement.StartInitialization();
        Assert.Equal(ssh.Id, browserFactory.CreatedConnections[^1]);
    }

    [Theory]
    [InlineData(PanelKind.Browser)]
    [InlineData(PanelKind.FileViewer)]
    [InlineData(PanelKind.Statistics)]
    [InlineData(PanelKind.ProcessMonitor)]
    public async Task New_tab_adapter_choices_append_single_panel_tabs(
        PanelKind kind)
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            browserRendererFactory: browserFactory);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var originalTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var originalPanelIds = originalTab.Panels.Select(panel => panel.Id).ToArray();
        var initialTabCount = runtime.Tabs.Count;

        var created = kind switch
        {
            PanelKind.Browser => await viewModel.AddBrowserTabAsync(),
            PanelKind.FileViewer => await viewModel.AddFileViewerTabAsync(),
            PanelKind.Statistics => await viewModel.AddStatisticsTabAsync(),
            PanelKind.ProcessMonitor => await viewModel.AddProcessMonitorTabAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        Assert.True(created);
        Assert.Equal(initialTabCount + 1, runtime.Tabs.Count);
        Assert.Equal(originalPanelIds, originalTab.Panels.Select(panel => panel.Id));
        var addedTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        Assert.NotSame(originalTab, addedTab);
        Assert.Equal(kind, Assert.Single(addedTab.Panels).Kind);
        Assert.Equal(
            addedTab.Id,
            Assert.IsType<WorkspaceGraphSnapshot>(
                recorder.CurrentWorkspace).Workspace.ActiveTabId);
    }

    [Fact]
    public async Task Browser_session_can_create_its_first_workspace_and_tab()
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            browserRendererFactory: browserFactory);

        Assert.True(viewModel.CanStartBrowserSession);
        Assert.False(viewModel.CanCreateBrowserPanel);

        Assert.True(await viewModel.OpenLocalBrowserWorkspaceAsync());

        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var browser = Assert.IsType<BrowserRuntimePanelViewModel>(tab.ActivePanel);
        Assert.Equal(BrowserAddress.Blank, browser.CurrentAddress);
        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
        Assert.True(viewModel.CanCreateBrowserPanel);
        Assert.Equal(1, browserFactory.CreateCount);

        var graph = Assert.IsType<WorkspaceGraphSnapshot>(
            recorder.CurrentWorkspace).Workspace;
        Assert.Equal(runtime.Id, graph.Id);
        Assert.Equal(tab.Id, graph.ActiveTabId);
        Assert.Equal(PanelKind.Browser, Assert.Single(graph.Tabs[0].Panels).Kind);
    }

    [Fact]
    public async Task Opening_workspace_accepts_host_reconciled_session_links()
    {
        var linkedSessionId = SessionId.New();
        var (client, recorder) = CreateSessionClient();
        recorder.NextRegistrationSessionId = linkedSessionId;
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        var proposal = Assert.Single(recorder.Registrations).Request.Workspace;
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var registered = Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace);
        Assert.Null(Assert.Single(
            proposal.Tabs[0].Panels,
            panel => panel.Id == registered.Workspace.Tabs[0].Panels[0].Id).SessionId);
        Assert.Equal(
            linkedSessionId,
            registered.Workspace.Tabs[0].Panels[0].SessionId);
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(1, runtime.HostSequence);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Opening_workspace_reconciles_an_accepted_graph_after_the_receipt_is_lost()
    {
        var (client, recorder) = CreateSessionClient();
        recorder.AcceptThenCancelNextRegistration = true;
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        var hosted = Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace);
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        Assert.Equal(hosted.Workspace.Id, runtime.Id);
        Assert.Equal(hosted.Workspace.Tabs.Select(tab => tab.Id), runtime.Tabs.Select(tab => tab.Id));
        Assert.Equal(hosted.Workspace.ActiveTabId, runtime.ActiveTab?.Id);
        Assert.Equal(hosted.Revision, runtime.HostRevision);
        Assert.Equal(hosted.LastSequence, runtime.HostSequence);
        Assert.Null(viewModel.OperationError);
    }

    [Theory]
    [InlineData(0L, 1L, 0L)]
    [InlineData(1L, 0L, 1L)]
    [InlineData(1L, 1L, 2L)]
    public async Task Opening_workspace_rejects_malformed_registration_cursors(
        long revision,
        long sequence,
        long resultingRevision)
    {
        var (client, recorder) = CreateSessionClient();
        recorder.NextRegistrationReceiptFactory = accepted =>
        {
            var malformed = new WorkspaceGraphSnapshot(
                accepted.WindowId,
                accepted.Workspace,
                revision,
                sequence);
            return HostResult<WorkspaceGraphSnapshot>.Succeed(
                malformed,
                resultingRevision);
        };
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());

        Assert.False(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        Assert.Null(viewModel.RuntimeWorkspace);
        Assert.Contains(
            "invalid workspace registration receipt",
            viewModel.OperationError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelled_registration_uses_a_bounded_lifetime_reconciliation_query()
    {
        var (client, recorder) = CreateSessionClient();
        recorder.DelayNextRegistration = true;
        recorder.StallNextWorkspaceQuery = true;
        using var cancellation = new CancellationTokenSource();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());

        var open = viewModel.OpenWorkspaceAsync(WorkspaceId, cancellation.Token);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await recorder.WorkspaceQueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(recorder.WorkspaceQueryTokenWasCancellationRequestedOnEntry);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => open.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(viewModel.RuntimeWorkspace);

        Assert.True(
            await viewModel.OpenWorkspaceAsync(WorkspaceId)
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancelled_initial_registration_disposes_the_unowned_runtime_graph()
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, recorder) = CreateSessionClient();
        recorder.DelayNextRegistration = true;
        using var cancellation = new CancellationTokenSource();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            browserRendererFactory: browserFactory);

        var open = viewModel.OpenWorkspaceAsync(WorkspaceId, cancellation.Token);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);

        Assert.Null(viewModel.RuntimeWorkspace);
        Assert.Null(recorder.CurrentWorkspace);
        Assert.Equal(0, browserFactory.CreateCount);
        Assert.Equal(0, browserFactory.DisposeCount);
    }

    [Fact]
    public async Task Failed_initial_registration_disposes_the_unowned_runtime_graph()
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, recorder) = CreateSessionClient();
        recorder.FailNextRegistrationWithTransportError = true;
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            browserRendererFactory: browserFactory);

        await Assert.ThrowsAsync<IOException>(
            () => viewModel.OpenWorkspaceAsync(WorkspaceId));

        Assert.Null(viewModel.RuntimeWorkspace);
        Assert.Null(recorder.CurrentWorkspace);
        Assert.Equal(0, browserFactory.CreateCount);
        Assert.Equal(0, browserFactory.DisposeCount);
    }

    [Fact]
    public async Task Rejected_initial_registration_disposes_the_unowned_runtime_graph()
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, recorder) = CreateSessionClient();
        recorder.RejectNextRegistration = true;
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            browserRendererFactory: browserFactory);

        Assert.False(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        Assert.Null(viewModel.RuntimeWorkspace);
        Assert.Null(recorder.CurrentWorkspace);
        Assert.Equal(0, browserFactory.CreateCount);
        Assert.Equal(0, browserFactory.DisposeCount);
    }

    [Fact]
    public async Task Failed_recovery_registration_disposes_the_unowned_runtime_graph()
    {
        string recoveryPayload;
        var (sourceClient, _) = CreateSessionClient();
        using (var source = CreateViewModel(
                   sourceClient,
                   CreateCatalogSnapshot(),
                   browserRendererFactory: new RecordingBrowserRendererViewFactory()))
        {
            Assert.True(await source.OpenWorkspaceAsync(WorkspaceId));
            recoveryPayload = RuntimeWorkspaceRecoveryCodec.Serialize(
                source.RuntimeWorkspace);
        }

        const string interruptedRunId = "runtime-graph-interrupted";
        var snapshot = new RuntimeRecoverySnapshot(
            interruptedRunId,
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            recoveryPayload,
            DateTimeOffset.UtcNow);

        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, recorder) = CreateSessionClient();
        recorder.FailNextRegistrationWithTransportError = true;
        using var recovered = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            browserRendererFactory: browserFactory);

        await Assert.ThrowsAsync<IOException>(
            () => recovered.RestoreRuntimeSnapshotsAsync([snapshot]));

        Assert.Null(recovered.RuntimeWorkspace);
        Assert.Null(recorder.CurrentWorkspace);
        Assert.Equal(0, browserFactory.CreateCount);
        Assert.Equal(0, browserFactory.DisposeCount);
    }

    [Fact]
    public async Task Host_rejection_leaves_tab_activation_unchanged()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var requestedTab = runtime.Tabs[1];
        recorder.RejectNextTabActivation = true;

        var activated = await viewModel.ActivateTabAsync(requestedTab.Id);

        Assert.False(activated);
        Assert.Same(originalTab, runtime.ActiveTab);
        Assert.Equal(1, runtime.HostRevision);
        Assert.Contains("revision_conflict", viewModel.OperationError, StringComparison.Ordinal);
        var call = Assert.Single(recorder.TabActivations);
        Assert.Equal(requestedTab.Id, call.Request.TabId);
        Assert.Equal(1, call.Context.ExpectedRevision);
    }

    [Fact]
    public async Task Already_focused_tab_and_panel_accept_same_cursor_no_op_receipts()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var panel = Assert.IsAssignableFrom<RuntimePanelViewModel>(tab.ActivePanel);

        Assert.True(await viewModel.ActivateTabAsync(tab.Id));
        Assert.True(await viewModel.ActivatePanelAsync(panel.Id));

        Assert.Same(tab, runtime.ActiveTab);
        Assert.Same(panel, tab.ActivePanel);
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(1, runtime.HostSequence);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Tab_activation_receipt_must_bind_the_requested_focus()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var requestedTab = runtime.Tabs[1];
        recorder.NextTabActivationReceiptFactory = accepted =>
        {
            var wrongFocus = new WorkspaceGraphSnapshot(
                accepted.WindowId,
                accepted.Workspace.ActivateTab(originalTab.Id),
                accepted.Revision,
                accepted.LastSequence);
            return HostResult<WorkspaceGraphSnapshot>.Succeed(
                wrongFocus,
                wrongFocus.Revision);
        };

        Assert.False(await viewModel.ActivateTabAsync(requestedTab.Id));

        Assert.Same(originalTab, runtime.ActiveTab);
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(1, runtime.HostSequence);
        Assert.Contains(
            "invalid tab activation receipt",
            viewModel.OperationError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0L, 2L, 0L)]
    [InlineData(2L, 0L, 2L)]
    [InlineData(1L, 2L, 1L)]
    [InlineData(2L, 1L, 2L)]
    [InlineData(2L, 2L, 3L)]
    public async Task Tab_activation_rejects_regressing_or_incoherent_cursors(
        long revision,
        long sequence,
        long resultingRevision)
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var requestedTab = runtime.Tabs[1];
        recorder.NextTabActivationReceiptFactory = accepted =>
        {
            var malformed = new WorkspaceGraphSnapshot(
                accepted.WindowId,
                accepted.Workspace,
                revision,
                sequence);
            return HostResult<WorkspaceGraphSnapshot>.Succeed(
                malformed,
                resultingRevision);
        };

        Assert.False(await viewModel.ActivateTabAsync(requestedTab.Id));

        Assert.Same(originalTab, runtime.ActiveTab);
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(1, runtime.HostSequence);
        Assert.Contains(
            "invalid tab activation receipt",
            viewModel.OperationError,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A placeholder holds a cell the host has never heard of, and the chooser
    /// that fills it sits over one. Reaching past it to another panel is the
    /// ordinary way out of that state, and it came back as an invalid receipt.
    /// </summary>
    [Fact]
    public async Task A_panel_can_be_activated_while_a_placeholder_holds_a_cell()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var requestedPanel = Assert.Single(
            tab.Panels,
            panel => panel.Kind == PanelKind.Browser);

        Assert.True(await viewModel.AddPlaceholderPanelAsync(PanelSide.Right));
        var placeholder = Assert.Single(tab.Panels.OfType<PanelPlaceholderViewModel>());
        Assert.Same(placeholder, tab.ActivePanel);

        Assert.True(await viewModel.ActivatePanelAsync(requestedPanel.Id));
        Assert.Same(requestedPanel, tab.ActivePanel);
        Assert.Null(viewModel.OperationError);
    }

    /// <summary>
    /// Asking what to open used to be a modal over the whole window. It is a tab
    /// now, holding one unanswered cell — so it lives in the workspace graph like
    /// any other tab, and the host holds the same cell the client draws.
    /// </summary>
    [Fact]
    public async Task A_launcher_tab_is_one_unanswered_cell()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var tabCount = runtime.Tabs.Count;

        Assert.True(await viewModel.AddLauncherTabAsync());

        Assert.Equal(tabCount + 1, runtime.Tabs.Count);
        var tab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var placeholder = Assert.IsType<PanelPlaceholderViewModel>(Assert.Single(tab.Panels));
        Assert.Same(placeholder, tab.ActivePanel);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task First_panel_selected_in_a_launcher_sets_its_tab_name_and_icon()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.AddLauncherTabAsync());
        var tab = viewModel.RuntimeWorkspace!.ActiveTab!;
        tab.ReplaceTarget = Assert.Single(tab.Panels).Id;

        Assert.True(await viewModel.AddDatabasePanelAsync());

        Assert.Equal("Database", tab.Title);
        Assert.Equal("database", tab.Icon);
        var hosted = Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace).Workspace;
        Assert.Equal(
            "Database",
            hosted.Tabs.Single(candidate => candidate.Id == hosted.ActiveTabId).Title);
    }

    [Theory]
    [InlineData(true, false, "Investigations", "database")]
    [InlineData(false, true, "Database", "star")]
    [InlineData(true, true, "Investigations", "star")]
    public async Task First_panel_preserves_each_manually_chosen_tab_identity_field(
        bool chooseTitle,
        bool chooseIcon,
        string expectedTitle,
        string expectedIcon)
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.AddLauncherTabAsync());
        var tab = viewModel.RuntimeWorkspace!.ActiveTab!;
        tab.ReplaceTarget = Assert.Single(tab.Panels).Id;

        Assert.True(await viewModel.UpdateRuntimeTabIdentityAsync(
            tab.Id,
            chooseTitle ? "Investigations" : tab.Title,
            chooseIcon ? "star" : tab.Icon));
        Assert.True(await viewModel.AddDatabasePanelAsync());

        Assert.Equal(expectedTitle, tab.Title);
        Assert.Equal(expectedIcon, tab.Icon);
        var hosted = Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace).Workspace;
        Assert.Equal(
            expectedTitle,
            hosted.Tabs.Single(candidate => candidate.Id == hosted.ActiveTabId).Title);
    }

    [Fact]
    public async Task Choosing_the_displayed_default_icon_still_claims_it_from_the_first_panel()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.AddLauncherTabAsync());
        var tab = viewModel.RuntimeWorkspace!.ActiveTab!;
        tab.ReplaceTarget = Assert.Single(tab.Panels).Id;
        var chosenIcon = tab.Icon;

        Assert.True(viewModel.ChooseRuntimeTabIcon(tab.Id, chosenIcon));
        Assert.True(await viewModel.AddDatabasePanelAsync());

        Assert.Equal("Database", tab.Title);
        Assert.Equal(chosenIcon, tab.Icon);
    }

    [Fact]
    public async Task Editing_a_tab_updates_host_title_and_local_icon_together()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var tab = viewModel.RuntimeWorkspace!.ActiveTab!;

        Assert.True(await viewModel.UpdateRuntimeTabIdentityAsync(
            tab.Id,
            "Investigations",
            "star"));

        Assert.Equal("Investigations", tab.Title);
        Assert.Equal("star", tab.Icon);
        var hosted = Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace).Workspace;
        Assert.Equal(
            "Investigations",
            hosted.Tabs.Single(candidate => candidate.Id == hosted.ActiveTabId).Title);
    }

    /// <summary>
    /// Closing tabs one by one used to arrive at a blank window with a button on
    /// it. The last one leaves the question "what do I open" in its place, so
    /// the workspace is never empty and never needs reopening — which is what
    /// brought a stale set of tabs back.
    /// </summary>
    [Fact]
    public async Task Closing_the_last_tab_leaves_the_launcher_in_its_place()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateSinglePanelCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var tab = Assert.Single(runtime.Tabs);

        Assert.True(await viewModel.RemoveTabAsync(tab.Id));

        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        var launcher = Assert.Single(runtime.Tabs);
        Assert.NotSame(tab, launcher);
        Assert.IsType<PanelPlaceholderViewModel>(Assert.Single(launcher.Panels));
        Assert.Null(viewModel.OperationError);
    }

    /// <summary>
    /// And that one stays. Closing it would leave a window with nothing in it
    /// and nothing to open anything from, and the workspace would have to be
    /// reopened — which is how a set of closed tabs came back. The strip stops
    /// offering to close it, and refuses if asked anyway.
    /// </summary>
    [Fact]
    public async Task The_last_launcher_tab_cannot_be_closed()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateSinglePanelCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        Assert.True(await viewModel.RemoveTabAsync(Assert.Single(runtime.Tabs).Id));
        var launcher = Assert.Single(runtime.Tabs);
        Assert.False(launcher.CanClose);

        Assert.False(await viewModel.RemoveTabAsync(launcher.Id));

        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        Assert.Same(launcher, Assert.Single(runtime.Tabs));
    }

    /// <summary>
    /// Closing a tab moves to the one before it. Jumping to the first tab sent
    /// the user across the strip every time they closed something near the end.
    /// </summary>
    [Fact]
    public async Task Closing_a_tab_activates_the_one_before_it()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        Assert.True(await viewModel.AddLauncherTabAsync());
        Assert.True(await viewModel.AddLauncherTabAsync());
        Assert.True(runtime.Tabs.Count >= 3);
        var last = runtime.Tabs[^1];
        var before = runtime.Tabs[^2];
        Assert.Same(last, runtime.ActiveTab);

        Assert.True(await viewModel.RemoveTabAsync(last.Id));

        Assert.Same(before, runtime.ActiveTab);
    }

    /// <summary>
    /// A launcher tab is the question; a saved screen is an answer to it. Opening
    /// beside it left the question sitting next to its own answer for the user to
    /// close by hand.
    /// </summary>
    [Fact]
    public async Task A_saved_screen_takes_over_the_launcher_tab_that_asked_for_it()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        Assert.True(await viewModel.AddLauncherTabAsync());
        var launcherTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var tabCount = runtime.Tabs.Count;
        var at = runtime.Tabs.IndexOf(launcherTab);

        Assert.True(await viewModel.LaunchScreenAsync(AppendedScreenId));

        Assert.Equal(tabCount, runtime.Tabs.Count);
        Assert.DoesNotContain(launcherTab, runtime.Tabs);
        Assert.Same(runtime.Tabs[at], runtime.ActiveTab);
        Assert.DoesNotContain(
            runtime.ActiveTab!.Panels,
            panel => panel is PanelPlaceholderViewModel);
        Assert.Null(viewModel.OperationError);
    }

    /// <summary>
    /// Anywhere else a saved screen still brings its own tab: only the tab that
    /// exists to ask the question gets taken over.
    /// </summary>
    [Fact]
    public async Task A_saved_screen_opens_beside_a_tab_that_holds_real_panels()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var existingTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var tabCount = runtime.Tabs.Count;

        Assert.True(await viewModel.LaunchScreenAsync(AppendedScreenId));

        Assert.Equal(tabCount + 1, runtime.Tabs.Count);
        Assert.Contains(existingTab, runtime.Tabs);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Panel_activation_receipt_must_bind_the_requested_tab_and_panel()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        var originalPanel = Assert.IsAssignableFrom<RuntimePanelViewModel>(
            tab.ActivePanel);
        var requestedPanel = Assert.Single(
            tab.Panels,
            panel => panel.Kind == PanelKind.Browser);
        recorder.NextPanelActivationReceiptFactory = accepted =>
        {
            var wrongFocus = new WorkspaceGraphSnapshot(
                accepted.WindowId,
                accepted.Workspace.ActivatePanel(tab.Id, originalPanel.Id),
                accepted.Revision,
                accepted.LastSequence);
            return HostResult<WorkspaceGraphSnapshot>.Succeed(
                wrongFocus,
                wrongFocus.Revision);
        };

        Assert.False(await viewModel.ActivatePanelAsync(requestedPanel.Id));

        Assert.Same(tab, runtime.ActiveTab);
        Assert.Same(originalPanel, tab.ActivePanel);
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(1, runtime.HostSequence);
        Assert.Contains(
            "invalid panel activation receipt",
            viewModel.OperationError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successive_tab_activations_are_serialized_and_chain_host_revisions()
    {
        var (client, recorder) = CreateSessionClient();
        recorder.DelayFirstTabActivation = true;
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);

        var activateBeta = viewModel.ActivateTabAsync(runtime.Tabs[1].Id);
        await recorder.FirstTabActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activateGamma = viewModel.ActivateTabAsync(runtime.Tabs[2].Id);
        recorder.AllowFirstTabActivation.TrySetResult();

        Assert.True(await activateBeta);
        Assert.True(await activateGamma);

        Assert.Equal(1, recorder.MaximumConcurrentTabActivations);
        Assert.Equal(
            [runtime.Tabs[1].Id, runtime.Tabs[2].Id],
            recorder.TabActivations.Select(call => call.Request.TabId));
        Assert.Equal(
            [1L, 2L],
            recorder.TabActivations.Select(call => call.Context.ExpectedRevision));
        Assert.Same(runtime.Tabs[2], runtime.ActiveTab);
        Assert.Equal(3, runtime.HostRevision);
    }

    [Fact]
    public async Task Structural_additions_propose_expected_revision_and_commit_only_after_acceptance()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var initialTabCount = runtime.Tabs.Count;
        var initialPanelCount = runtime.ActiveTab!.Panels.Count;
        recorder.RejectNextRegistration = true;

        Assert.False(await viewModel.AddFilePanelAsync());

        Assert.Equal(initialPanelCount, runtime.ActiveTab.Panels.Count);
        Assert.Equal(1, runtime.HostRevision);
        var rejectedProposal = recorder.Registrations[1];
        Assert.Equal(1L, rejectedProposal.Context.ExpectedRevision);
        Assert.Equal(
            initialPanelCount + 1,
            rejectedProposal.Request.Workspace.Tabs[0].Panels.Count);

        recorder.DelayNextRegistration = true;
        var addPanel = viewModel.AddFilePanelAsync();
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(initialPanelCount, runtime.ActiveTab.Panels.Count);
        Assert.Equal(1, runtime.HostRevision);

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await addPanel);
        Assert.Equal(initialPanelCount + 1, runtime.ActiveTab.Panels.Count);
        Assert.Equal(2, runtime.HostRevision);

        Assert.True(await viewModel.AddLocalTerminalTabAsync());

        Assert.Equal(initialTabCount + 1, runtime.Tabs.Count);
        Assert.Same(runtime.Tabs[^1], runtime.ActiveTab);
        Assert.Equal(3, runtime.HostRevision);
        Assert.Equal(
            [null, 1L, 1L, 2L],
            recorder.Registrations.Select(call => call.Context.ExpectedRevision));
        Assert.Equal(
            initialTabCount + 1,
            recorder.Registrations[^1].Request.Workspace.Tabs.Count);
    }

    [Theory]
    [InlineData(1L, 2L)]
    [InlineData(2L, 1L)]
    public async Task Structural_replacement_requires_both_graph_cursors_to_advance(
        long revision,
        long sequence)
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalPanelIds = runtime.ActiveTab!.Panels.Select(panel => panel.Id).ToArray();
        recorder.NextRegistrationReceiptFactory = accepted =>
        {
            var malformed = new WorkspaceGraphSnapshot(
                accepted.WindowId,
                accepted.Workspace,
                revision,
                sequence);
            return HostResult<WorkspaceGraphSnapshot>.Succeed(
                malformed,
                malformed.Revision);
        };

        Assert.False(await viewModel.AddFilePanelAsync());

        Assert.Equal(originalPanelIds, runtime.ActiveTab.Panels.Select(panel => panel.Id));
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(1, runtime.HostSequence);
        Assert.Contains(
            "invalid File Viewer creation receipt",
            viewModel.OperationError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Queued_final_panel_removal_preserves_a_panel_added_by_the_earlier_mutation()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateSinglePanelCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var tab = Assert.Single(runtime.Tabs);
        var originalPanel = Assert.Single(tab.Panels);
        recorder.DelayNextRegistration = true;

        var addPanel = viewModel.AddFilePanelAsync();
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var removePanel = viewModel.RemovePanelAsync(originalPanel.Id);
        await Task.Yield();

        Assert.False(addPanel.IsCompleted);
        Assert.False(removePanel.IsCompleted);
        Assert.Same(originalPanel, Assert.Single(tab.Panels));

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await addPanel);
        Assert.True(await removePanel);
        var addedPanel = Assert.IsType<FileRuntimePanelViewModel>(
            Assert.Single(tab.Panels));

        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        Assert.Same(tab, Assert.Single(runtime.Tabs));
        Assert.Same(addedPanel, tab.ActivePanel);
        Assert.Equal(3, runtime.HostRevision);
        Assert.Empty(recorder.Unregistrations);
        Assert.Equal(
            [null, 1L, 2L],
            recorder.Registrations.Select(call => call.Context.ExpectedRevision));

        var removalProposal = recorder.Registrations[^1].Request.Workspace;
        var hostedTab = Assert.Single(removalProposal.Tabs);
        Assert.Equal([addedPanel.Id], hostedTab.Panels.Select(panel => panel.Id));
        Assert.Equal(addedPanel.Id, hostedTab.ActivePanelId);
        var hosted = Assert.IsType<WorkspaceGraphSnapshot>(
            recorder.CurrentWorkspace).Workspace;
        Assert.Equal(
            tab.Panels.Select(panel => panel.Id),
            Assert.Single(hosted.Tabs).Panels.Select(panel => panel.Id));
    }

    [Fact]
    public async Task Shell_routes_and_subroutes_expose_exactly_one_visible_surface()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());

        Assert.Equal(1, VisibleRouteCount(viewModel));
        Assert.True(viewModel.IsWorkspaceVisible);

        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            viewModel.ShowSettings(page);

            Assert.Equal(1, VisibleRouteCount(viewModel));
            Assert.True(viewModel.IsSettingsVisible);
            Assert.Equal(1, VisibleSettingsPageCount(viewModel));
        }

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.Equal(1, VisibleRouteCount(viewModel));
        Assert.True(viewModel.IsWorkspaceVisible);

        foreach (var overlay in Enum.GetValues<ShellOverlay>())
        {
            viewModel.ShowOverlay(overlay);

            Assert.Equal(
                overlay == ShellOverlay.None ? 0 : 1,
                VisibleOverlayCount(viewModel));
        }
    }

    [Theory]
    [InlineData(ShellOverlay.CommandPalette)]
    [InlineData(ShellOverlay.NewPanel)]
    public async Task Navigation_dismisses_clean_transient_overlays(
        ShellOverlay overlay)
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.ShowOverlay(overlay);

        viewModel.ShowSettings(SettingsPage.Files);

        Assert.Equal(ShellRoute.Settings, viewModel.Route);
        Assert.Equal(SettingsPage.Files, viewModel.SettingsPage);
        Assert.Equal(ShellOverlay.None, viewModel.Overlay);
        Assert.Equal(1, VisibleRouteCount(viewModel));
        Assert.Equal(0, VisibleOverlayCount(viewModel));
    }

    [Fact]
    public async Task Dirty_layout_designer_blocks_navigation_and_preserves_its_surface()
    {
        var snapshot = CreateCatalogSnapshot();
        var layout = Assert.Single(snapshot.Layouts).Value;
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, snapshot);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.BeginEditLayout(layout.Id);
        var editor = Assert.IsType<LayoutDesignerViewModel>(
            viewModel.LayoutDesignerEditor);
        editor.Name = $"{editor.Name} revised";

        viewModel.ShowSettings(SettingsPage.Appearance);

        Assert.True(editor.IsDirty);
        Assert.Same(editor, viewModel.LayoutDesignerEditor);
        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
        Assert.Equal(ShellOverlay.LayoutDesigner, viewModel.Overlay);
        Assert.Contains("discard", viewModel.OperationError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ShellOverlay.CommandPalette)]
    public async Task Saved_tab_completion_closes_its_initiating_overlay_and_shows_workspace(
        ShellOverlay overlay)
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.ShowSettings(SettingsPage.Files);
        viewModel.ShowOverlay(overlay);

        Assert.True(await viewModel.LaunchConnectionAsync(AppendedConnectionId));

        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
        Assert.Equal(ShellOverlay.None, viewModel.Overlay);
        Assert.Equal(1, VisibleRouteCount(viewModel));
        Assert.Equal(0, VisibleOverlayCount(viewModel));
    }

    /// <summary>
    /// The panel chooser offers saved connections, not only blank adapters, so
    /// choosing one has to open that connection rather than the default local
    /// shell the terminal tile would have opened.
    /// </summary>
    [Fact]
    public async Task A_saved_connection_opens_as_a_panel_titled_after_that_connection()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.ShowOverlay(ShellOverlay.NewPanel);
        var before = viewModel.RuntimeWorkspace!.ActiveTab!.Panels.Count;

        Assert.True(await viewModel.AddConnectionPanelAsync(AppendedConnectionId));

        var tab = viewModel.RuntimeWorkspace!.ActiveTab!;
        Assert.Equal(before + 1, tab.Panels.Count);

        var added = Assert.IsType<TerminalRuntimePanelViewModel>(tab.ActivePanel);
        Assert.Equal(AppendedConnectionId, added.ConnectionId);

        // Completing the choice closes the overlay it was made from.
        Assert.Equal(ShellOverlay.None, viewModel.Overlay);
        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
    }

    [Fact]
    public async Task DockerShellButtonOpensAFullTerminalTab()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            dockerEngineClient: new SingleContainerDockerClient());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.AddDockerPanelAsync());
        var docker = Assert.IsType<DockerRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace!.ActiveTab!.ActivePanel);
        await docker.Initialization;
        var before = viewModel.RuntimeWorkspace.Tabs.Count;

        Assert.True(await viewModel.OpenDockerContainerShellAsync(docker));

        Assert.Equal(before + 1, viewModel.RuntimeWorkspace.Tabs.Count);
        var shell = Assert.IsType<TerminalRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace.ActiveTab!.ActivePanel);
        await shell.Initialization;
        Assert.Equal("api shell", viewModel.RuntimeWorkspace.ActiveTab.Title);
        Assert.Equal(
            TerminalShellActivityFallback.PromptShape,
            shell.SessionRequest?.Launch.ShellActivityFallback);
        Assert.Equal(
            [DockerContainerShellCommand.Build("container-api", "/bin/ash")],
            shell.StartupCommands);
    }

    [Fact]
    public async Task DockerInlineShellUsesTheDockerPanelAsItsCloseScopeOwner()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            dockerEngineClient: new SingleContainerDockerClient());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.AddDockerPanelAsync());
        var docker = Assert.IsType<DockerRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace!.ActiveTab!.ActivePanel);
        await docker.Initialization;

        Assert.True(await viewModel.OpenDockerContainerInlineShellAsync(docker));

        var shell = Assert.IsType<TerminalRuntimePanelViewModel>(docker.InlineShell);
        await shell.Initialization;
        var owner = Assert.IsType<SessionOwner>(typeof(TerminalRuntimePanelViewModel)
            .GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(shell));
        Assert.Equal(docker.Id, owner.PanelId);
        Assert.Equal(PanelSessionRole.Embedded, shell.SessionRole);
        Assert.Equal(PanelSessionRole.Embedded, shell.SessionRequest?.Role);
        Assert.Equal(
            TerminalShellActivityFallback.PromptShape,
            shell.SessionRequest?.Launch.ShellActivityFallback);
        Assert.Equal(docker.Id, shell.StartupCommandDispatchState.PanelId);
        Assert.Equal(
            [DockerContainerShellCommand.Build("container-api", "/bin/ash")],
            shell.StartupCommands);
    }

    [Fact]
    public async Task DockerRemoteSwitchUsesTheSshTargetForInventoryAndInlineShell()
    {
        var ssh = new ConnectionProfile(
            new ConnectionId("docker-ssh"),
            ConnectionProfile.CurrentSchemaVersion,
            "Remote Docker",
            new ConnectionEndpoint.Ssh("docker.example.test", username: "ops"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var snapshot = CreateCatalogSnapshot();
        snapshot = snapshot with
        {
            Connections = snapshot.Connections.Append(Store(ssh)).ToArray(),
        };
        var dockerClient = new SingleContainerDockerClient();
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            snapshot,
            dockerEngineClient: dockerClient);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.AddDockerPanelAsync());
        var local = Assert.IsType<DockerRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace!.ActiveTab!.ActivePanel);
        await local.Initialization;

        Assert.True(viewModel.ReplacePanelConnection(local, ssh));

        var remote = Assert.IsType<DockerRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace.ActiveTab.ActivePanel);
        await remote.Initialization;
        Assert.Equal(ssh.Id, remote.ConnectionId);
        Assert.Equal(ssh.Id, dockerClient.ReadConnections[^1]);

        Assert.True(await viewModel.OpenDockerContainerInlineShellAsync(remote));

        var shell = Assert.IsType<TerminalRuntimePanelViewModel>(remote.InlineShell);
        await shell.Initialization;
        Assert.Equal(ssh.Id, shell.ConnectionId);
        Assert.Equal(PanelSessionRole.Embedded, shell.SessionRequest?.Role);
        Assert.Equal(
            TerminalShellActivityFallback.PromptShape,
            shell.SessionRequest?.Launch.ShellActivityFallback);
        Assert.Equal(remote.Id, shell.StartupCommandDispatchState.PanelId);
    }

    [Fact]
    public async Task MissingContainerShellIsPresentedInsideTheDockerViewport()
    {
        var unavailable = new DockerError(
            DockerErrorCode.ShellUnavailable,
            "This container has no supported interactive shell.",
            false);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            dockerEngineClient: new SingleContainerDockerClient(
                new DockerResult<string>.Failure(unavailable)));
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.AddDockerPanelAsync());
        var docker = Assert.IsType<DockerRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace!.ActiveTab!.ActivePanel);
        await docker.Initialization;

        Assert.False(await viewModel.OpenDockerContainerInlineShellAsync(docker));

        Assert.Null(viewModel.OperationError);
        Assert.Null(docker.InlineShell);
        Assert.True(docker.IsShellDetail);
        Assert.Equal("No interactive shell found", docker.ShellStateTitle);
        Assert.Equal(unavailable.Message, docker.ShellStateMessage);
        Assert.True(docker.CanRetryShell);
    }

    [Fact]
    public async Task Opening_a_deleted_connection_as_a_panel_reports_it_and_adds_nothing()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var before = viewModel.RuntimeWorkspace!.ActiveTab!.Panels.Count;

        Assert.False(await viewModel.AddConnectionPanelAsync(new ConnectionId("gone")));

        Assert.Equal(before, viewModel.RuntimeWorkspace!.ActiveTab!.Panels.Count);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.OperationError));
    }

    [Fact]
    public async Task New_panel_completion_closes_its_initiating_overlay_and_shows_workspace()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.ShowOverlay(ShellOverlay.NewPanel);

        Assert.True(await viewModel.AddFilePanelAsync());

        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
        Assert.Equal(ShellOverlay.None, viewModel.Overlay);
        Assert.Equal(1, VisibleRouteCount(viewModel));
        Assert.Equal(0, VisibleOverlayCount(viewModel));
    }

    [Fact]
    public async Task Delayed_saved_tab_completion_does_not_steal_newer_settings_route()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.ShowOverlay(ShellOverlay.CommandPalette);
        recorder.DelayNextRegistration = true;

        var append = viewModel.LaunchConnectionAsync(AppendedConnectionId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ShowSettings(SettingsPage.Files);

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await append);

        Assert.Equal(ShellRoute.Settings, viewModel.Route);
        Assert.Equal(SettingsPage.Files, viewModel.SettingsPage);
        Assert.Equal(ShellOverlay.None, viewModel.Overlay);
        Assert.Equal("Secondary local", viewModel.RuntimeWorkspace?.ActiveTab?.Title);
    }

    [Fact]
    public async Task Delayed_saved_tab_completion_does_not_close_a_newer_overlay()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.ShowOverlay(ShellOverlay.CommandPalette);
        recorder.DelayNextRegistration = true;

        var append = viewModel.LaunchScreenAsync(AppendedScreenId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ShowOverlay(ShellOverlay.NewPanel);

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await append);

        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
        Assert.Equal(ShellOverlay.NewPanel, viewModel.Overlay);
        Assert.True(viewModel.IsNewPanelVisible);
        Assert.Equal("Operations screen", viewModel.RuntimeWorkspace?.ActiveTab?.Title);
    }

    [Fact]
    public async Task Saved_connection_and_screen_launches_append_host_registered_tabs()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.Equal("New Session", viewModel.NewItemLauncherTitle);
        viewModel.LauncherSearchQuery = "Secondary local";
        Assert.Equal(
            "Open",
            Assert.Single(
                viewModel.LauncherSearchResults,
                result => result.Target
                    == new LauncherSearchTarget.Connection(AppendedConnectionId))
                .TrailingText);

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalTabs = runtime.Tabs.ToArray();
        var originalPanels = originalTabs
            .SelectMany(tab => tab.Panels)
            .ToArray();
        Assert.Equal("New Tab", viewModel.NewItemLauncherTitle);
        Assert.Equal(
            "Add tab",
            Assert.Single(
                viewModel.LauncherSearchResults,
                result => result.Target
                    == new LauncherSearchTarget.Connection(AppendedConnectionId))
                .TrailingText);

        Assert.True(await viewModel.LaunchConnectionAsync(AppendedConnectionId));
        var connectionTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        Assert.Equal("Secondary local", connectionTab.Title);
        Assert.Equal(
            AppendedConnectionId,
            Assert.IsType<TerminalRuntimePanelViewModel>(
                Assert.Single(connectionTab.Panels)).ConnectionId);

        Assert.True(await viewModel.LaunchScreenAsync(AppendedScreenId));
        var screenTab = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);

        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        Assert.Equal(originalTabs.Length + 2, runtime.Tabs.Count);
        Assert.All(
            originalTabs.Select((tab, index) => (tab, index)),
            item => Assert.Same(item.tab, runtime.Tabs[item.index]));
        Assert.All(
            originalPanels,
            panel => Assert.Contains(
                runtime.Tabs.SelectMany(tab => tab.Panels),
                candidate => ReferenceEquals(panel, candidate)));
        Assert.Equal("Operations screen", screenTab.Title);
        Assert.Equal(3, screenTab.Columns);
        Assert.Equal(1, screenTab.Rows);
        Assert.Equal(
            [PanelKind.Terminal, PanelKind.Terminal],
            screenTab.Panels.Select(panel => panel.Kind));
        Assert.All(
            screenTab.Panels.Cast<TerminalRuntimePanelViewModel>(),
            panel => Assert.Equal(AppendedConnectionId, panel.ConnectionId));
        Assert.Same(screenTab, runtime.ActiveTab);
        Assert.Equal(3, runtime.HostRevision);
        Assert.Equal(
            [null, 1L, 2L],
            recorder.Registrations.Select(call => call.Context.ExpectedRevision));
        Assert.Equal(
            runtime.Tabs.Select(tab => tab.Id),
            recorder.Registrations[^1].Request.Workspace.Tabs.Select(tab => tab.Id));
        Assert.Equal(
            screenTab.Id,
            recorder.Registrations[^1].Request.Workspace.ActiveTabId);
        Assert.Equal(
            [AppendedConnectionId],
            runtime.Connections.Select(connection => connection.Id));
        Assert.Empty(recorder.Unregistrations);
    }

    [Fact]
    public async Task Delayed_saved_connection_append_preserves_a_newer_dirty_workspace_editor()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var initialTabCount = runtime.Tabs.Count;
        viewModel.ShowOverlay(ShellOverlay.CommandPalette);
        recorder.DelayNextRegistration = true;

        var append = viewModel.LaunchConnectionAsync(AppendedConnectionId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.BeginEditWorkspace(WorkspaceId);
        var editor = Assert.IsType<WorkspaceEditorViewModel>(viewModel.WorkspaceEditor);
        Assert.True(editor.AddConnection(AppendedConnectionId).IsSuccess);
        Assert.True(editor.IsDirty);

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await append);

        Assert.Equal(initialTabCount + 1, runtime.Tabs.Count);
        Assert.Equal("Secondary local", runtime.ActiveTab?.Title);
        Assert.Same(editor, viewModel.WorkspaceEditor);
        Assert.True(editor.IsDirty);
        Assert.Equal(ShellOverlay.DefinitionEditor, viewModel.Overlay);
        Assert.True(viewModel.IsDefinitionEditorVisible);
    }

    [Fact]
    public async Task Delayed_saved_screen_append_preserves_a_newer_dirty_layout_designer()
    {
        var snapshot = CreateTabAppendCatalogSnapshot();
        var layout = Assert.Single(snapshot.Layouts).Value;
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, snapshot);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var initialTabCount = runtime.Tabs.Count;
        viewModel.ShowOverlay(ShellOverlay.CommandPalette);
        recorder.DelayNextRegistration = true;

        var append = viewModel.LaunchScreenAsync(AppendedScreenId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.BeginEditLayout(layout.Id);
        var designer = Assert.IsType<LayoutDesignerViewModel>(
            viewModel.LayoutDesignerEditor);
        designer.Name = $"{designer.Name} revised";
        Assert.True(designer.IsDirty);

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await append);

        Assert.Equal(initialTabCount + 1, runtime.Tabs.Count);
        Assert.Equal("Operations screen", runtime.ActiveTab?.Title);
        Assert.Same(designer, viewModel.LayoutDesignerEditor);
        Assert.True(designer.IsDirty);
        Assert.Equal(ShellOverlay.LayoutDesigner, viewModel.Overlay);
        Assert.True(viewModel.IsLayoutDesignerVisible);
    }

    [Fact]
    public async Task Saved_screen_panels_start_only_after_the_host_accepts_the_append()
    {
        var files = new EmptyFileClients(exposeLocalProfile: true);
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateDeferredPanelAppendCatalogSnapshot(),
            filePanelClient: files,
            fileTransferQueueClient: files);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        recorder.DelayNextRegistration = true;

        var append = viewModel.LaunchScreenAsync(AppendedScreenId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, files.ListCallCount);
        Assert.Equal(0, recorder.FilePanelEnsureCount);
        Assert.Equal(0, recorder.StatisticsEnsureCount);
        Assert.DoesNotContain(runtime.Tabs, tab => tab.Title == "Deferred panels");

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await append);
        await WaitForAsync(() =>
            recorder.FilePanelEnsureCount == 1
            && recorder.StatisticsEnsureCount == 1);

        Assert.Equal(0, files.ListCallCount);
        var appended = Assert.IsType<RuntimeTabViewModel>(runtime.ActiveTab);
        Assert.Equal("Deferred panels", appended.Title);
        Assert.IsType<FileRuntimePanelViewModel>(
            Assert.Single(appended.Panels, panel => panel.Kind == PanelKind.FileViewer));
        Assert.IsType<StatisticsRuntimePanelViewModel>(
            Assert.Single(appended.Panels, panel => panel.Kind == PanelKind.Statistics));
    }

    [Fact]
    public async Task Rejected_saved_screen_append_never_starts_provisional_panels()
    {
        var files = new EmptyFileClients(exposeLocalProfile: true);
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateDeferredPanelAppendCatalogSnapshot(),
            filePanelClient: files,
            fileTransferQueueClient: files);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var initialTabIds = runtime.Tabs.Select(tab => tab.Id).ToArray();
        recorder.RejectNextRegistration = true;

        Assert.False(await viewModel.LaunchScreenAsync(AppendedScreenId));

        Assert.Equal(0, files.ListCallCount);
        Assert.Equal(0, recorder.FilePanelEnsureCount);
        Assert.Equal(0, recorder.StatisticsEnsureCount);
        Assert.Equal(initialTabIds, runtime.Tabs.Select(tab => tab.Id));
    }

    [Fact]
    public async Task Cancelled_saved_screen_append_never_starts_provisional_panels()
    {
        var files = new EmptyFileClients(exposeLocalProfile: true);
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateDeferredPanelAppendCatalogSnapshot(),
            filePanelClient: files,
            fileTransferQueueClient: files);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var initialTabIds = runtime.Tabs.Select(tab => tab.Id).ToArray();
        recorder.DelayNextRegistration = true;
        using var cancellation = new CancellationTokenSource();

        var append = viewModel.LaunchScreenAsync(
            AppendedScreenId,
            cancellation.Token);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => append);

        Assert.Equal(0, files.ListCallCount);
        Assert.Equal(0, recorder.FilePanelEnsureCount);
        Assert.Equal(0, recorder.StatisticsEnsureCount);
        Assert.Equal(initialTabIds, runtime.Tabs.Select(tab => tab.Id));
    }

    [Fact]
    public async Task Concurrent_saved_tab_launches_serialize_without_dropping_either_append()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var initialTabIds = runtime.Tabs.Select(tab => tab.Id).ToArray();
        recorder.DelayNextRegistration = true;

        var connectionAppend = viewModel.LaunchConnectionAsync(AppendedConnectionId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var screenAppend = viewModel.LaunchScreenAsync(AppendedScreenId);
        await Task.Yield();

        Assert.False(connectionAppend.IsCompleted);
        Assert.False(screenAppend.IsCompleted);
        Assert.Equal(initialTabIds, runtime.Tabs.Select(tab => tab.Id));

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await connectionAppend);
        Assert.True(await screenAppend);

        Assert.Equal(initialTabIds.Length + 2, runtime.Tabs.Count);
        Assert.Equal(initialTabIds, runtime.Tabs.Take(initialTabIds.Length).Select(tab => tab.Id));
        Assert.Equal(
            [null, 1L, 2L],
            recorder.Registrations.Select(call => call.Context.ExpectedRevision));
        var firstAppend = recorder.Registrations[1].Request.Workspace;
        var secondAppend = recorder.Registrations[2].Request.Workspace;
        Assert.Equal(initialTabIds.Length + 1, firstAppend.Tabs.Count);
        Assert.Equal(initialTabIds.Length + 2, secondAppend.Tabs.Count);
        Assert.Equal(
            firstAppend.Tabs.Select(tab => tab.Id),
            secondAppend.Tabs.Take(firstAppend.Tabs.Count).Select(tab => tab.Id));
        Assert.Equal(
            runtime.Tabs.Select(tab => tab.Id),
            secondAppend.Tabs.Select(tab => tab.Id));
        Assert.Equal(3, runtime.HostRevision);
        Assert.Equal(runtime.ActiveTab?.Id, secondAppend.ActiveTabId);
    }

    [Fact]
    public async Task Saved_tab_append_and_tab_removal_serialize_without_losing_the_append()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var removedTab = runtime.Tabs[1];
        var retainedTabs = runtime.Tabs
            .Where(tab => !ReferenceEquals(tab, removedTab))
            .ToArray();
        var initialTabCount = runtime.Tabs.Count;
        recorder.DelayNextRegistration = true;

        var append = viewModel.LaunchConnectionAsync(AppendedConnectionId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remove = viewModel.RemoveTabAsync(removedTab.Id);
        await Task.Yield();

        Assert.False(append.IsCompleted);
        Assert.False(remove.IsCompleted);
        Assert.Contains(removedTab, runtime.Tabs);

        recorder.AllowDelayedRegistration.TrySetResult();
        Assert.True(await append);
        Assert.True(await remove);

        var appendedTab = Assert.Single(
            runtime.Tabs,
            tab => tab.Title == "Secondary local");
        Assert.Equal(initialTabCount, runtime.Tabs.Count);
        Assert.DoesNotContain(runtime.Tabs, tab => tab.Id == removedTab.Id);
        Assert.All(retainedTabs, tab => Assert.Contains(tab, runtime.Tabs));
        Assert.Contains(appendedTab, runtime.Tabs);
        Assert.Same(appendedTab, runtime.ActiveTab);
        Assert.Equal(3, runtime.HostRevision);
        Assert.Equal(
            [null, 1L, 2L],
            recorder.Registrations.Select(call => call.Context.ExpectedRevision));

        var appendProposal = recorder.Registrations[1].Request.Workspace;
        var removalProposal = recorder.Registrations[2].Request.Workspace;
        Assert.Contains(appendProposal.Tabs, tab => tab.Id == appendedTab.Id);
        Assert.Contains(removalProposal.Tabs, tab => tab.Id == appendedTab.Id);
        Assert.DoesNotContain(removalProposal.Tabs, tab => tab.Id == removedTab.Id);
        Assert.Equal(appendedTab.Id, removalProposal.ActiveTabId);

        var hosted = Assert.IsType<WorkspaceGraphSnapshot>(
            recorder.CurrentWorkspace).Workspace;
        Assert.Equal(
            runtime.Tabs.Select(tab => tab.Id),
            hosted.Tabs.Select(tab => tab.Id));
        Assert.Equal(appendedTab.Id, hosted.ActiveTabId);
        Assert.All(
            runtime.Tabs.Zip(hosted.Tabs),
            pair =>
            {
                Assert.Equal(
                    pair.First.Panels.Select(panel => panel.Id),
                    pair.Second.Panels.Select(panel => panel.Id));
                Assert.Equal(pair.First.ActivePanelId, pair.Second.ActivePanelId);
            });
    }

    /// <summary>
    /// A saved screen may point a monitor panel at a remote host, and it opens
    /// there. The sampler has run over a connection since the launcher started
    /// offering Statistics and Process monitor as ways to open one; only this
    /// path still answered "remote system monitoring is unavailable" and made
    /// you edit the screen to get a local panel you had not asked for.
    /// </summary>
    [Fact]
    public async Task A_saved_monitor_panel_opens_on_the_connection_it_names()
    {
        var remoteId = new ConnectionId("connections.monitored-host");
        var remote = new ConnectionProfile(
            remoteId,
            ConnectionProfile.CurrentSchemaVersion,
            "Monitored host",
            new ConnectionEndpoint.Ssh("metrics.example.test", username: "ops"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var baseline = CreateCatalogSnapshot();
        var monitored = new WorkspaceDefinition(
            new WorkspaceId("runtime-graph-monitored"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Monitored",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("monitored-tab"),
                    "Monitored",
                    Assert.Single(baseline.Layouts).Value.Id,
                    [
                        Panel(
                            "monitored-stats",
                            "left",
                            ScreenPanelKind.Statistics,
                            "Statistics",
                            remoteId),
                        Panel(
                            "monitored-processes",
                            "right",
                            ScreenPanelKind.ProcessMonitor,
                            "Processes",
                            remoteId),
                    ]),
            ]);
        var snapshot = baseline with
        {
            Connections = baseline.Connections.Append(Store(remote)).ToArray(),
            Workspaces = baseline.Workspaces.Append(Store(monitored)).ToArray(),
        };
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, snapshot);

        Assert.True(await viewModel.OpenWorkspaceAsync(monitored.Id));

        var tab = Assert.Single(viewModel.RuntimeWorkspace!.Tabs);
        var statistics = Assert.IsType<StatisticsRuntimePanelViewModel>(tab.Panels[0]);
        var processes = Assert.IsType<ProcessMonitorRuntimePanelViewModel>(tab.Panels[1]);
        Assert.Equal(remoteId, statistics.ConnectionId);
        Assert.Equal(remoteId, processes.ConnectionId);
    }

    [Fact]
    public void Saved_connection_shortcuts_are_projected_from_target_capabilities()
    {
        var sshId = new ConnectionId("connections.production-ssh");
        var s3Id = new FileProviderProfileId("files.production-s3");
        var ssh = new ConnectionProfile(
            sshId,
            ConnectionProfile.CurrentSchemaVersion,
            "Production SSH",
            new ConnectionEndpoint.Ssh("ssh.example.test", username: "deploy"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var s3 = new FileProviderProfile(
            s3Id,
            FileProviderProfile.CurrentSchemaVersion,
            "Production objects",
            new FileProviderConfiguration.S3("production-objects"));
        var snapshot = CreateCatalogSnapshot() with
        {
            Connections = CreateCatalogSnapshot().Connections.Append(Store(ssh)).ToArray(),
            FileProviderProfiles = [Store(s3)],
        };
        var files = new EmptyFileClients(
        [
            FileProfile(
                s3Id,
                s3.Name,
                FileProviderFamily.S3,
                "production-objects"),
        ]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            snapshot,
            filePanelClient: files,
            fileTransferQueueClient: files);

        var sshShortcut = Assert.Single(
            viewModel.SavedConnectionShortcuts,
            shortcut => shortcut.Target
                is PanelConnectionOptionViewModel.Target.Connection target
                && target.Id == sshId);
        Assert.Equal(PanelKind.Terminal, sshShortcut.DefaultLaunch.Panel);
        Assert.Equal(
            [PanelKind.FileViewer, PanelKind.Statistics, PanelKind.ProcessMonitor, PanelKind.Docker],
            sshShortcut.AlternativeLaunches.Select(launch => launch.Panel));
        Assert.True(sshShortcut.HasAlternatives);

        var s3Shortcut = Assert.Single(
            viewModel.SavedConnectionShortcuts,
            shortcut => shortcut.Target
                is PanelConnectionOptionViewModel.Target.FileProvider target
                && target.Id == s3Id);
        Assert.Equal(PanelKind.FileViewer, s3Shortcut.DefaultLaunch.Panel);
        Assert.Empty(s3Shortcut.AlternativeLaunches);
        Assert.False(s3Shortcut.HasAlternatives);
        Assert.True(s3Shortcut.CanOpen);
    }

    [Fact]
    public async Task Saved_connection_file_action_appends_a_bound_file_viewer_tab()
    {
        var files = new EmptyFileClients(
        [
            FileProfile(
                BuiltInFileProviders.HomeId,
                "Local",
                FileProviderFamily.Posix,
                "local"),
        ]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            filePanelClient: files,
            fileTransferQueueClient: files);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var connection = Assert.Single(viewModel.SavedConnectionShortcuts);
        var launch = Assert.Single(
            connection.AlternativeLaunches,
            candidate => candidate.Panel == PanelKind.FileViewer);

        Assert.True(await viewModel.AddSavedConnectionTabAsync(launch));

        Assert.Equal(4, workspace.Tabs.Count);
        var panel = Assert.IsType<FileRuntimePanelViewModel>(
            workspace.ActiveTab!.ActivePanel);
        Assert.Equal(PanelKind.FileViewer, panel.Kind);
        Assert.Equal(connection.Name, panel.ConnectionDisplayName);
    }

    /// <summary>
    /// The same row means two things depending on where it was clicked. From a
    /// cell the user has already placed, choosing an adapter has to fill that
    /// cell — opening a tab instead left the cell empty and put the panel
    /// somewhere the user was not looking.
    /// </summary>
    [Fact]
    public async Task Saved_connection_file_action_fills_a_placed_cell_rather_than_a_tab()
    {
        var files = new EmptyFileClients(
        [
            FileProfile(
                BuiltInFileProviders.HomeId,
                "Local",
                FileProviderFamily.Posix,
                "local"),
        ]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            filePanelClient: files,
            fileTransferQueueClient: files);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var tabCount = workspace.Tabs.Count;
        var tab = workspace.ActiveTab!;
        var panelCount = tab.Panels.Count;
        var launch = Assert.Single(
            Assert.Single(viewModel.SavedConnectionShortcuts).AlternativeLaunches,
            candidate => candidate.Panel == PanelKind.FileViewer);

        Assert.True(await viewModel.AddSavedConnectionPanelAsync(launch));

        Assert.Equal(tabCount, workspace.Tabs.Count);
        Assert.Same(tab, workspace.ActiveTab);
        Assert.Equal(panelCount + 1, tab.Panels.Count);
        Assert.Equal(
            PanelKind.FileViewer,
            Assert.IsType<FileRuntimePanelViewModel>(tab.ActivePanel).Kind);
    }

    [Fact]
    public async Task File_only_shortcut_opens_its_saved_provider_in_a_new_tab()
    {
        var s3Id = new FileProviderProfileId("files.production-s3");
        var s3 = new FileProviderProfile(
            s3Id,
            FileProviderProfile.CurrentSchemaVersion,
            "Production objects",
            new FileProviderConfiguration.S3("production-objects"));
        var snapshot = CreateCatalogSnapshot() with
        {
            FileProviderProfiles = [Store(s3)],
        };
        var files = new EmptyFileClients(
        [
            FileProfile(
                s3Id,
                s3.Name,
                FileProviderFamily.S3,
                "production-objects"),
        ]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            snapshot,
            filePanelClient: files,
            fileTransferQueueClient: files);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var shortcut = Assert.Single(
            viewModel.SavedConnectionShortcuts,
            candidate => candidate.Target
                is PanelConnectionOptionViewModel.Target.FileProvider target
                && target.Id == s3Id);

        Assert.True(await viewModel.AddSavedConnectionTabAsync(
            shortcut.DefaultLaunch));

        var panel = Assert.IsType<FileRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace!.ActiveTab!.ActivePanel);
        Assert.True(panel.UsesProfile(s3Id));
        Assert.Equal("Production objects", viewModel.RuntimeWorkspace.ActiveTab.Title);
    }

    [Fact]
    public async Task Queued_saved_screen_launch_revalidates_the_definition_under_the_graph_gate()
    {
        var (client, recorder) = CreateSessionClient();
        var snapshot = CreateTabAppendCatalogSnapshot();
        var catalog = DispatchProxy.Create<IDefinitionCatalog, FixedCatalogProxy>();
        var catalogProxy = (FixedCatalogProxy)(object)catalog;
        catalogProxy.Snapshot = snapshot;
        var files = new EmptyFileClients();
        using var viewModel = new MainWindowViewModel(
            client,
            catalog,
            new SuccessfulConnectionRuntime(),
            new EmptySecretVault(),
            files,
            files,
            new TerminalStartupCommandDispatcher(
                new SuccessfulAuditStore(),
                TimeProvider.System));
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var initialTabCount = runtime.Tabs.Count;
        recorder.DelayNextRegistration = true;

        var blocker = viewModel.LaunchConnectionAsync(AppendedConnectionId);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = viewModel.LaunchScreenAsync(AppendedScreenId);
        catalogProxy.Snapshot = snapshot with { Screens = [] };

        recorder.AllowDelayedRegistration.TrySetResult();

        Assert.True(await blocker);
        Assert.False(await queued);
        Assert.Equal(initialTabCount + 1, runtime.Tabs.Count);
        Assert.Equal(2, recorder.Registrations.Count);
        Assert.Contains(
            "no longer exists",
            viewModel.OperationError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_saved_screen_append_preserves_the_live_graph()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalActiveTab = runtime.ActiveTab;
        var originalTabIds = runtime.Tabs.Select(tab => tab.Id).ToArray();
        var originalPanelIds = runtime.Tabs
            .SelectMany(tab => tab.Panels)
            .Select(panel => panel.Id)
            .ToArray();
        recorder.RejectNextRegistration = true;

        Assert.False(await viewModel.LaunchScreenAsync(AppendedScreenId));

        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        Assert.Same(originalActiveTab, runtime.ActiveTab);
        Assert.Equal(originalTabIds, runtime.Tabs.Select(tab => tab.Id));
        Assert.Equal(
            originalPanelIds,
            runtime.Tabs.SelectMany(tab => tab.Panels).Select(panel => panel.Id));
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(2, recorder.Registrations.Count);
        Assert.Equal(1L, recorder.Registrations[^1].Context.ExpectedRevision);
        Assert.Equal(
            originalTabIds.Length + 1,
            recorder.Registrations[^1].Request.Workspace.Tabs.Count);
        Assert.Equal(
            originalTabIds,
            Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace)
                .Workspace.Tabs.Select(tab => tab.Id));
        Assert.Contains("revision_conflict", viewModel.OperationError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_saved_screen_append_preserves_the_live_graph()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalActiveTab = runtime.ActiveTab;
        var originalTabIds = runtime.Tabs.Select(tab => tab.Id).ToArray();
        var originalPanelIds = runtime.Tabs
            .SelectMany(tab => tab.Panels)
            .Select(panel => panel.Id)
            .ToArray();
        recorder.DelayNextRegistration = true;
        using var cancellation = new CancellationTokenSource();

        var append = viewModel.LaunchScreenAsync(
            AppendedScreenId,
            cancellation.Token);
        await recorder.DelayedRegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(originalTabIds, runtime.Tabs.Select(tab => tab.Id));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => append);

        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        Assert.Same(originalActiveTab, runtime.ActiveTab);
        Assert.Equal(originalTabIds, runtime.Tabs.Select(tab => tab.Id));
        Assert.Equal(
            originalPanelIds,
            runtime.Tabs.SelectMany(tab => tab.Panels).Select(panel => panel.Id));
        Assert.Equal(1, runtime.HostRevision);
        Assert.Equal(
            originalTabIds,
            Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace)
                .Workspace.Tabs.Select(tab => tab.Id));
    }

    [Fact]
    public async Task Accepted_saved_screen_append_with_a_lost_receipt_reconciles_from_the_host()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateTabAppendCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var originalTabIds = runtime.Tabs.Select(tab => tab.Id).ToArray();
        recorder.AcceptThenCancelNextRegistration = true;

        Assert.True(await viewModel.LaunchScreenAsync(AppendedScreenId));

        var hosted = Assert.IsType<WorkspaceGraphSnapshot>(recorder.CurrentWorkspace);
        Assert.Equal(originalTabIds.Length + 1, runtime.Tabs.Count);
        Assert.Equal(
            hosted.Workspace.Tabs.Select(tab => tab.Id),
            runtime.Tabs.Select(tab => tab.Id));
        Assert.Equal(hosted.Workspace.ActiveTabId, runtime.ActiveTab?.Id);
        Assert.Equal(hosted.Revision, runtime.HostRevision);
        Assert.Equal(hosted.LastSequence, runtime.HostSequence);
        Assert.Null(viewModel.OperationError);
    }

    /// <summary>
    /// Emptying the last tab leaves the launcher standing in it rather than
    /// taking the workspace down: what was asked for was to close a panel, and
    /// the answer to "what goes here now" belongs in the space it left.
    /// </summary>
    [Fact]
    public async Task Removing_final_panel_leaves_the_launcher_in_the_tab()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateSinglePanelCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var panelId = Assert.Single(Assert.Single(runtime.Tabs).Panels).Id;

        Assert.True(await viewModel.RemovePanelAsync(panelId));

        Assert.Empty(recorder.Unregistrations);
        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        Assert.IsType<PanelPlaceholderViewModel>(
            Assert.Single(Assert.Single(runtime.Tabs).Panels));
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Session_link_event_advances_revision_before_the_next_activation()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await recorder.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var linkedSessionId = SessionId.New();
        recorder.LinkFirstPanelSession(linkedSessionId, publishEvent: true);
        await WaitForAsync(() => runtime.HostRevision == 2);

        Assert.Equal(2, runtime.HostSequence);
        Assert.True(await viewModel.ActivateTabAsync(runtime.Tabs[1].Id));
        var activation = Assert.Single(recorder.TabActivations);
        Assert.Equal(2L, activation.Context.ExpectedRevision);
        Assert.Equal(3, runtime.HostRevision);
    }

    [Fact]
    public async Task Revision_conflict_refreshes_a_missed_session_link_before_retrying()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await recorder.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        recorder.LinkFirstPanelSession(SessionId.New(), publishEvent: false);

        Assert.True(await viewModel.ActivateTabAsync(runtime.Tabs[1].Id));
        Assert.Equal(
            [1L, 2L],
            recorder.TabActivations.Select(call => call.Context.ExpectedRevision));
        Assert.Equal(3, runtime.HostRevision);
        Assert.Equal(3, runtime.HostSequence);
    }

    [Fact]
    public async Task Revision_conflict_retry_preserves_host_refreshed_active_tab_and_panel()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var alpha = runtime.Tabs[0];
        var beta = runtime.Tabs[1];
        var removedPanel = Assert.Single(
            alpha.Panels,
            panel => panel.Kind == PanelKind.Browser);
        var refreshedAlphaPanel = Assert.Single(
            alpha.Panels,
            panel => panel.Kind == PanelKind.FileViewer);
        var refreshedBetaPanel = Assert.Single(beta.Panels);
        var missedClient = new ClientId("missed-active-state");

        var panelActivation = await client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                runtime.Id,
                alpha.Id,
                refreshedAlphaPanel.Id),
            OperationContext.ForHuman(missedClient, expectedRevision: 1),
            CancellationToken.None);
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(
            panelActivation);
        var tabActivation = await client.ActivateWorkspaceTabAsync(
            new ActivateWorkspaceTabRequest(runtime.Id, beta.Id),
            OperationContext.ForHuman(missedClient, expectedRevision: 2),
            CancellationToken.None);
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(
            tabActivation);

        Assert.True(await viewModel.RemovePanelAsync(removedPanel.Id));

        Assert.Equal(
            [null, 1L, 3L],
            recorder.Registrations.Select(call => call.Context.ExpectedRevision));
        var staleProposal = recorder.Registrations[1].Request.Workspace;
        Assert.Equal(alpha.Id, staleProposal.ActiveTabId);
        var retryProposal = recorder.Registrations[2].Request.Workspace;
        Assert.Equal(beta.Id, retryProposal.ActiveTabId);
        Assert.Equal(
            refreshedAlphaPanel.Id,
            Assert.Single(retryProposal.Tabs, tab => tab.Id == alpha.Id)
                .ActivePanelId);
        Assert.Equal(
            refreshedBetaPanel.Id,
            Assert.Single(retryProposal.Tabs, tab => tab.Id == beta.Id)
                .ActivePanelId);
        Assert.Same(beta, runtime.ActiveTab);
        Assert.Same(refreshedBetaPanel, beta.ActivePanel);
        Assert.Same(refreshedAlphaPanel, alpha.ActivePanel);
        Assert.DoesNotContain(alpha.Panels, panel => panel.Id == removedPanel.Id);
        Assert.Equal(4, runtime.HostRevision);
        Assert.Equal(4, runtime.HostSequence);
    }

    [Fact]
    public async Task Disposing_the_view_model_cancels_the_workspace_watch()
    {
        var (client, recorder) = CreateSessionClient();
        var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        await recorder.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();

        await recorder.WatchStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, recorder.ActiveWatchCount);
    }

    [Fact]
    public async Task Presentation_teardown_requires_ui_thread_access_before_releasing_panels()
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var dispatcher = new RecordingUiThreadDispatcher(hasAccess: false);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            dispatcher,
            browserRendererFactory: browserFactory);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));

        var error = Assert.Throws<InvalidOperationException>(
            viewModel.TeardownPresentationForShutdown);

        Assert.Contains("UI thread", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, dispatcher.VerifyCount);
        Assert.Equal(0, browserFactory.DisposeCount);
    }

    [Fact]
    public async Task Presentation_teardown_releases_every_open_workspace_once()
    {
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var dispatcher = new RecordingUiThreadDispatcher(hasAccess: true);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            dispatcher,
            browserRendererFactory: browserFactory);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        Assert.True(await viewModel.OpenWorkspaceAsync(SecondWorkspaceId));
        Assert.True(await viewModel.AddBrowserPanelAsync());
        Assert.Equal(2, viewModel.OpenWorkspaces.Count);
        Assert.Equal(2, browserFactory.CreateCount);
        Assert.Equal(0, browserFactory.DisposeCount);

        viewModel.TeardownPresentationForShutdown();
        viewModel.TeardownPresentationForShutdown();

        Assert.Equal(2, dispatcher.VerifyCount);
        Assert.Equal(2, browserFactory.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_quiescence_cancels_a_graph_item_waiting_for_ui_dispatch()
    {
        var (client, recorder) = CreateSessionClient();
        var dispatcher = new BlockingUiThreadDispatcher();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            dispatcher);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await recorder.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        recorder.LinkFirstPanelSession(SessionId.New(), publishEvent: true);
        await dispatcher.InvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.QuiesceForShutdownAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await recorder.WatchStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, recorder.ActiveWatchCount);
        Assert.Equal(1, runtime.HostRevision);
    }

    [Fact]
    public async Task Dispatcher_shutdown_cancellation_does_not_fault_graph_watch_quiescence()
    {
        var (client, recorder) = CreateSessionClient();
        var dispatcher = new CancellingUiThreadDispatcher();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            dispatcher);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        await recorder.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        recorder.LinkFirstPanelSession(SessionId.New(), publishEvent: true);
        await dispatcher.InvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await recorder.WatchStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await viewModel.QuiesceForShutdownAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, recorder.ActiveWatchCount);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Workspace_resynchronization_restarts_the_watch_from_the_authoritative_cursor()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await recorder.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        recorder.LinkFirstPanelSession(SessionId.New(), publishEvent: false);

        recorder.PublishResynchronization();
        await WaitForAsync(() => runtime.HostSequence == 2);
        await WaitForAsync(() => recorder.WatchStartCount == 2);

        recorder.LinkFirstPanelSession(SessionId.New(), publishEvent: true);
        await WaitForAsync(() => runtime.HostSequence == 3);

        Assert.Equal(3, runtime.HostRevision);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task A_cancelled_old_workspace_watch_cannot_report_an_error_on_its_replacement()
    {
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(client, CreateCatalogSnapshot());
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var original = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await recorder.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        recorder.FailWatchWhenCancelled = true;

        // A different workspace, because reopening the same one now brings the
        // workspace already open back to the front rather than replacing it —
        // switching view is not closing anything. The watch still has to hand
        // over, which is what this test is about.
        Assert.True(await viewModel.OpenWorkspaceAsync(SecondWorkspaceId));

        var replacement = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await WaitForAsync(() => recorder.WatchStartCount == 2);
        await recorder.WatchStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task Quick_terminal_uses_an_independent_window_ownership_boundary()
    {
        var snapshot = CreateCatalogSnapshot();
        var (client, recorder) = CreateSessionClient();
        using var mainWindow = CreateViewModel(client, snapshot);
        using var quickTerminal = new QuickTerminalViewModel(
            mainWindow,
            CreateFixedCatalog(snapshot),
            new SuccessfulConnectionRuntime());

        await quickTerminal.Initialization;

        var request = Assert.IsType<EnsureTerminalSessionRequest>(quickTerminal.TerminalRequest);
        Assert.Equal(quickTerminal.WindowId, request.Owner.WindowId);
        Assert.NotEqual(mainWindow.WindowId, request.Owner.WindowId);
        Assert.Equal(quickTerminal.WorkspaceId, request.Owner.WorkspaceId);
        Assert.NotEmpty(recorder.Registrations);
        Assert.All(
            recorder.Registrations,
            item => Assert.Equal(quickTerminal.WorkspaceId, item.Request.Workspace.Id));
        var registration = recorder.Registrations[^1];
        Assert.Equal(quickTerminal.WindowId, registration.Request.WindowId);
        Assert.Equal(quickTerminal.WorkspaceId, registration.Request.Workspace.Id);
        var graphTab = Assert.Single(registration.Request.Workspace.Tabs);
        Assert.Equal(request.Owner.TabId, graphTab.Id);
        Assert.Equal(request.Owner.PanelId, Assert.Single(graphTab.Panels).Id);
    }

    [Fact]
    public async Task Quick_terminal_tabs_own_independent_sessions_and_can_be_activated()
    {
        var snapshot = CreateCatalogSnapshot();
        var (client, _) = CreateSessionClient();
        using var mainWindow = CreateViewModel(client, snapshot);
        using var quickTerminal = new QuickTerminalViewModel(
            mainWindow,
            CreateFixedCatalog(snapshot),
            new SuccessfulConnectionRuntime());
        await quickTerminal.Initialization;
        var firstTab = Assert.Single(quickTerminal.Tabs);
        var firstRequest = Assert.IsType<EnsureTerminalSessionRequest>(
            firstTab.TerminalRequest);

        await quickTerminal.AddTabAsync();

        Assert.Equal(2, quickTerminal.Tabs.Count);
        var secondTab = Assert.IsType<QuickTerminalTabViewModel>(quickTerminal.ActiveTab);
        var secondRequest = Assert.IsType<EnsureTerminalSessionRequest>(
            secondTab.TerminalRequest);
        Assert.NotEqual(firstRequest.SessionId, secondRequest.SessionId);
        Assert.NotEqual(firstRequest.Owner.TabId, secondRequest.Owner.TabId);
        Assert.True(firstTab.CanClose);
        Assert.True(secondTab.CanClose);

        quickTerminal.MoveTab(secondTab, firstTab, placeAfterAnchor: false);

        Assert.Same(secondTab, quickTerminal.Tabs[0]);
        Assert.Same(firstTab, quickTerminal.Tabs[1]);

        quickTerminal.ActivateTab(firstTab);

        Assert.Same(firstTab, quickTerminal.ActiveTab);
        Assert.Same(firstRequest, quickTerminal.TerminalRequest);
    }

    [Fact]
    public async Task Quick_terminal_recovery_recreates_tab_order_and_selection()
    {
        var snapshot = CreateCatalogSnapshot();
        var connectionId = snapshot.Connections[0].Value.Id.Value;
        var (client, _) = CreateSessionClient();
        using var mainWindow = CreateViewModel(client, snapshot);
        using var quickTerminal = new QuickTerminalViewModel(
            mainWindow,
            CreateFixedCatalog(snapshot),
            new SuccessfulConnectionRuntime());

        await quickTerminal.RestoreAsync(new QuickTerminalRecoveryPayload(
            [connectionId, connectionId, connectionId],
            ActiveTabIndex: 1));

        Assert.Equal(3, quickTerminal.Tabs.Count);
        Assert.Same(quickTerminal.Tabs[1], quickTerminal.ActiveTab);
        Assert.All(
            quickTerminal.Tabs,
            tab => Assert.Equal(connectionId, tab.ConnectionId?.Value));
        Assert.Equal(3, quickTerminal.TerminalRequests.Count);
    }

    [Fact]
    public async Task Quick_terminal_agent_targets_its_own_workspace_and_active_panel()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var snapshot = CreateCatalogSnapshot();
        var (client, _) = CreateSessionClient();
        using var mainWindow = CreateViewModel(client, snapshot);
        using var quickTerminal = new QuickTerminalViewModel(
            mainWindow,
            CreateFixedCatalog(snapshot),
            new SuccessfulConnectionRuntime(),
            agentRuntime,
            aiProfiles);
        await quickTerminal.Initialization;
        var tab = Assert.IsType<QuickTerminalTabViewModel>(quickTerminal.ActiveTab);
        quickTerminal.AgentChat!.Prompt = "Inspect Quick Terminal.";

        await quickTerminal.SendAgentPromptAsync();

        var request = Assert.IsType<GovernedAgentPrompt>(agentRuntime.LastRequest);
        var target = Assert.IsType<AgentTarget.Panel>(request.Target);
        Assert.Equal(quickTerminal.WindowId, target.WindowId);
        Assert.Equal(quickTerminal.WorkspaceId, target.WorkspaceId);
        Assert.Equal(tab.Id, target.TabId);
        Assert.Equal(tab.PanelId, target.PanelId);
        Assert.NotEqual(mainWindow.WindowId, target.WindowId);
    }

    [Fact]
    public async Task Quick_terminal_agent_uses_its_rendered_bottom_right_chrome_pivot()
    {
        var snapshot = CreateCatalogSnapshot();
        var (client, _) = CreateSessionClient();
        using var mainWindow = CreateViewModel(client, snapshot);
        using var quickTerminal = new QuickTerminalViewModel(
            mainWindow,
            CreateFixedCatalog(snapshot),
            new SuccessfulConnectionRuntime());

        await quickTerminal.Initialization;

        Assert.False(quickTerminal.IsAgentPanelOnLeft);
        Assert.True(quickTerminal.IsAgentPanelOnRight);
        Assert.True(quickTerminal.IsAgentPanelAnchoredBottom);
        Assert.False(quickTerminal.IsAgentPanelAnchoredTop);
        Assert.Equal(
            Avalonia.Layout.VerticalAlignment.Bottom,
            quickTerminal.AgentPanelVerticalAlignment);
        Assert.Equal(
            Avalonia.Controls.Dock.Right,
            quickTerminal.AgentPanelDock);
    }

    [Fact]
    public async Task Agent_send_uses_exact_active_panel_and_shared_desktop_principal()
    {
        var provider = new AiProviderProfileDescriptor(
            new AiProviderProfileId("agent-provider"),
            "Agent provider",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://provider.example.test/v1/"),
            "model",
            Order: 0,
            IsEnabled: true,
            RequiresCredential: false);
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var desktopClientId = new ClientId("desktop-client");
        var principal = new FixedApprovalPrincipal(
            new ActorDescriptor(
                new ActorId("desktop-user"),
                ActorKind.Human,
                "Desktop user",
                desktopClientId));
        var (client, recorder) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles,
            approvalPrincipal: principal);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(workspace.ActiveTab);
        var panel = Assert.IsType<TerminalRuntimePanelViewModel>(tab.ActivePanel);
        viewModel.AgentChat!.Prompt = "Inspect the active terminal.";

        await viewModel.SendAgentPromptAsync();

        var request = Assert.IsType<GovernedAgentPrompt>(agentRuntime.LastRequest);
        var target = Assert.IsType<AgentTarget.Panel>(request.Target);
        Assert.Equal(provider.Id, request.ProviderId);
        Assert.Null(request.Policy);
        Assert.Equal("Inspect the active terminal.", request.Message);
        Assert.Equal(viewModel.WindowId, target.WindowId);
        Assert.Equal(workspace.Id, target.WorkspaceId);
        Assert.Equal(tab.Id, target.TabId);
        Assert.Equal(panel.Id, target.PanelId);
        Assert.Equal(desktopClientId, viewModel.ClientId);
        Assert.Equal(
            desktopClientId,
            Assert.Single(recorder.Registrations).Context.Actor.ClientId);
    }

    [Fact]
    public async Task Agent_steering_reuses_bound_run_without_resolving_a_new_workspace_target()
    {
        var provider = CreateAgentProvider();
        var runId = new AgentRunId("run-steering");
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        agentRuntime.SetSnapshot(
            agentRuntime.Snapshot with
            {
                State = GovernedAgentState.StreamingProvider,
                RunId = runId,
                ProviderId = provider.Id,
                Target = new AgentTarget.ConnectionSession(
                    new SessionId("session-steering")),
                TargetTitle = "Bound terminal",
                SteeringAvailable = true,
                SteeringGeneration = 17,
            });
        viewModel.AgentChat!.Prompt = "Check the canary first.";

        await viewModel.SendAgentPromptAsync();

        var steering = Assert.IsType<GovernedAgentSteering>(
            agentRuntime.LastSteering);
        Assert.Equal(runId, steering.RunId);
        Assert.Equal(17, steering.ExpectedGeneration);
        Assert.Equal("Check the canary first.", steering.Update);
        Assert.Equal(1, agentRuntime.SteeringCount);
        Assert.Equal(0, agentRuntime.SendCount);
        Assert.Null(agentRuntime.LastRequest);
        Assert.Equal(string.Empty, viewModel.AgentChat.Prompt);
    }

    [Fact]
    public async Task SavedScreenPolicyIsCapturedByRevisionAndSurvivesCatalogEditAndDeletion()
    {
        var acceptedPolicy = Policy(
            "agent-provider",
            "saved-model",
            capability => capability switch
            {
                AgentCapability.RunCommands => AgentPermission.Off,
                AgentCapability.ReadFiles => AgentPermission.Auto,
                _ => AgentPermission.Ask,
            });
        var editedPolicy = Policy(
            "edited-provider",
            "edited-model",
            _ => AgentPermission.Auto);
        var snapshot = CreateAgentPolicyCatalogSnapshot(acceptedPolicy, acceptedPolicy);
        var catalog = CreateFixedCatalog(snapshot);
        var catalogProxy = (FixedCatalogProxy)(object)catalog;
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            catalog,
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        var storedScreen = snapshot.Screens[0];

        Assert.True(await viewModel.OpenScreenAsync(storedScreen.Value.Id));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        await AwaitTerminalPanelPlansAsync(runtime);
        var tab = Assert.Single(runtime.Tabs);
        var acceptedSource = Assert.Single(tab.AgentPolicy.Sources);
        Assert.Equal(storedScreen.Value.Key, acceptedSource.Definition);
        Assert.Equal(5, acceptedSource.Revision);
        AssertPolicyEqual(acceptedPolicy, tab.AgentPolicy.EffectivePolicy);

        var current = storedScreen.Value;
        var editedScreen = new ScreenDefinition(
            current.Id,
            current.SchemaVersion,
            current.Name,
            current.Description,
            current.LayoutId,
            current.Panels,
            current.Tags,
            editedPolicy);
        catalogProxy.Snapshot = snapshot with
        {
            Screens =
            [
                Store(editedScreen, revision: 99),
                snapshot.Screens[1],
            ],
        };
        AssertPolicyEqual(acceptedPolicy, tab.AgentPolicy.EffectivePolicy);
        catalogProxy.Snapshot = catalogProxy.Snapshot with { Screens = [] };

        viewModel.AgentChat!.Prompt = "Inspect under the captured screen policy.";
        await viewModel.SendAgentPromptAsync();

        var request = Assert.IsType<GovernedAgentPrompt>(agentRuntime.LastRequest);
        AssertPolicyEqual(
            acceptedPolicy,
            Assert.IsType<AgentPolicy>(request.Policy));
        AssertPolicyEqual(acceptedPolicy, tab.AgentPolicy.EffectivePolicy);
    }

    [Fact]
    public async Task BroadAgentScopesUsePerCapabilityLeastPrivilegeFromCapturedTabs()
    {
        var firstPolicy = Policy(
            "agent-provider",
            "shared-model",
            capability => capability switch
            {
                AgentCapability.RunCommands => AgentPermission.Ask,
                AgentCapability.EditFiles => AgentPermission.Off,
                _ => AgentPermission.Auto,
            });
        var secondPolicy = Policy(
            "agent-provider",
            "shared-model",
            capability => capability switch
            {
                AgentCapability.RunCommands => AgentPermission.Off,
                AgentCapability.EditFiles => AgentPermission.Auto,
                _ => AgentPermission.Ask,
            });
        var expectedBroadPolicy = AgentPolicyResolver.ResolveLeastPrivilege(
            [firstPolicy, secondPolicy]);
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentPolicyCatalogSnapshot(firstPolicy, secondPolicy),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);

        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        await ActivateTerminalPanelsAsync(workspace);
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Collection(
            workspace.Tabs,
            first =>
            {
                AssertPolicyEqual(firstPolicy, first.AgentPolicy.EffectivePolicy);
                Assert.Equal(
                    [
                        (WorkspaceDefinition.Kind, WorkspaceId.Value, 3L),
                        (ScreenDefinition.Kind, "agent-policy-first", 5L),
                    ],
                    first.AgentPolicy.Sources.Select(source => (
                        source.Definition.Kind,
                        source.Definition.Value,
                        source.Revision)));
            },
            second =>
            {
                AssertPolicyEqual(secondPolicy, second.AgentPolicy.EffectivePolicy);
                Assert.Equal(
                    [
                        (WorkspaceDefinition.Kind, WorkspaceId.Value, 3L),
                        (ScreenDefinition.Kind, "agent-policy-second", 6L),
                    ],
                    second.AgentPolicy.Sources.Select(source => (
                        source.Definition.Kind,
                        source.Definition.Value,
                        source.Revision)));
            });

        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.CurrentTab);
        viewModel.AgentChat!.Prompt = "Inspect the current tab.";
        await viewModel.SendAgentPromptAsync();
        AssertPolicyEqual(
            firstPolicy,
            Assert.IsType<AgentPolicy>(agentRuntime.LastRequest!.Policy));

        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.Workspace);
        viewModel.AgentChat.Prompt = "Inspect the workspace.";
        await viewModel.SendAgentPromptAsync();
        var broadPolicy = Assert.IsType<AgentPolicy>(
            agentRuntime.LastRequest!.Policy);
        AssertPolicyEqual(expectedBroadPolicy, broadPolicy);
        Assert.Equal(AgentPermission.Off, broadPolicy.GetPermission(
            AgentCapability.RunCommands));
        Assert.Equal(AgentPermission.Off, broadPolicy.GetPermission(
            AgentCapability.EditFiles));

        Assert.Equal(2, viewModel.AgentTerminalSelectionOptions.Count);
        foreach (var option in viewModel.AgentTerminalSelectionOptions)
        {
            option.IsSelected = true;
        }

        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.SelectedPanels);
        viewModel.AgentChat.Prompt = "Inspect the selected terminals.";
        await viewModel.SendAgentPromptAsync();

        Assert.IsType<AgentTarget.SelectedPanels>(agentRuntime.LastRequest!.Target);
        AssertPolicyEqual(
            expectedBroadPolicy,
            Assert.IsType<AgentPolicy>(agentRuntime.LastRequest.Policy));
        Assert.Equal(3, agentRuntime.SendCount);
    }

    [Fact]
    public async Task HeterogeneousBroadPolicyScopeFailsBeforeGovernedRuntime()
    {
        var firstPolicy = Policy(
            "agent-provider",
            "shared-model",
            _ => AgentPermission.Ask);
        var secondPolicy = Policy(
            "other-provider",
            "shared-model",
            _ => AgentPermission.Off);
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentPolicyCatalogSnapshot(firstPolicy, secondPolicy),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.Workspace);
        viewModel.AgentChat!.Prompt = "Do not select a provider for me.";

        await viewModel.SendAgentPromptAsync();

        Assert.Equal(0, agentRuntime.SendCount);
        Assert.Null(agentRuntime.LastRequest);
        Assert.Contains(
            "different saved agent policy providers or models",
            viewModel.AgentChat.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BroadScopeMixingExplicitAndInheritedPolicyFailsBeforeRuntime()
    {
        var explicitPolicy = Policy(
            "agent-provider",
            "saved-model",
            _ => AgentPermission.Ask);
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentPolicyCatalogSnapshot(explicitPolicy, secondPolicy: null),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.Workspace);
        viewModel.AgentChat!.Prompt = "Inspect mixed policy provenance.";

        await viewModel.SendAgentPromptAsync();

        Assert.Equal(0, agentRuntime.SendCount);
        Assert.Null(agentRuntime.LastRequest);
        Assert.Contains(
            "saved policy overrides with inherited provider settings",
            viewModel.AgentChat.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_active_panel_scope_routes_a_live_browser_panel()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles,
            browserRendererFactory: browserFactory);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(workspace.ActiveTab);
        var browser = Assert.IsType<BrowserRuntimePanelViewModel>(
            Assert.Single(
                tab.Panels,
                panel => panel.Kind == PanelKind.Browser));
        Assert.NotNull(browser.SessionRequest);
        Assert.True(await viewModel.ActivatePanelAsync(browser.Id));
        viewModel.AgentChat!.Prompt = "Inspect the active browser.";

        await viewModel.SendAgentPromptAsync();

        var request = Assert.IsType<GovernedAgentPrompt>(agentRuntime.LastRequest);
        var target = Assert.IsType<AgentTarget.Panel>(request.Target);
        Assert.Equal(provider.Id, request.ProviderId);
        Assert.Equal("Inspect the active browser.", request.Message);
        Assert.Equal(viewModel.WindowId, target.WindowId);
        Assert.Equal(workspace.Id, target.WorkspaceId);
        Assert.Equal(tab.Id, target.TabId);
        Assert.Equal(browser.Id, target.PanelId);
    }

    [Fact]
    public async Task Agent_active_panel_scope_routes_a_hosted_local_process_monitor()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, recorder) = CreateSessionClient();
        recorder.AcceptProcessMonitorSessions = true;
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);

        Assert.True(await viewModel.OpenLocalMonitorWorkspaceAsync(
            PanelKind.ProcessMonitor));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(workspace.ActiveTab);
        var processMonitor = Assert.IsType<ProcessMonitorRuntimePanelViewModel>(
            tab.ActivePanel);
        await WaitForAsync(() => processMonitor.HasHostedSession);
        viewModel.AgentChat!.Prompt = "Inspect the bounded local process list.";

        await viewModel.SendAgentPromptAsync();

        var request = Assert.IsType<GovernedAgentPrompt>(agentRuntime.LastRequest);
        var target = Assert.IsType<AgentTarget.Panel>(request.Target);
        Assert.Equal(provider.Id, request.ProviderId);
        Assert.Equal("Inspect the bounded local process list.", request.Message);
        Assert.Equal(viewModel.WindowId, target.WindowId);
        Assert.Equal(workspace.Id, target.WorkspaceId);
        Assert.Equal(tab.Id, target.TabId);
        Assert.Equal(processMonitor.Id, target.PanelId);
    }

    [Fact]
    public async Task Agent_send_routes_visible_tab_and_workspace_scope_choices()
    {
        var provider = new AiProviderProfileDescriptor(
            new AiProviderProfileId("agent-provider"),
            "Agent provider",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://provider.example.test/v1/"),
            "model",
            Order: 0,
            IsEnabled: true,
            RequiresCredential: false);
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var browserFactory = new RecordingBrowserRendererViewFactory();
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles,
            browserRendererFactory: browserFactory);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        var tab = Assert.IsType<RuntimeTabViewModel>(workspace.ActiveTab);
        Assert.Contains(
            tab.Panels,
            panel => panel is BrowserRuntimePanelViewModel);

        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.CurrentTab);
        viewModel.AgentChat!.Prompt = "Inspect this tab.";
        await viewModel.SendAgentPromptAsync();

        var tabTarget = Assert.IsType<AgentTarget.OpenTab>(
            agentRuntime.LastRequest!.Target);
        Assert.Equal(viewModel.WindowId, tabTarget.WindowId);
        Assert.Equal(workspace.Id, tabTarget.WorkspaceId);
        Assert.Equal(tab.Id, tabTarget.TabId);

        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.Workspace);
        viewModel.AgentChat.Prompt = "Inspect this workspace.";
        await viewModel.SendAgentPromptAsync();

        var workspaceTarget = Assert.IsType<AgentTarget.Workspace>(
            agentRuntime.LastRequest!.Target);
        Assert.Equal(viewModel.WindowId, workspaceTarget.WindowId);
        Assert.Equal(workspace.Id, workspaceTarget.WorkspaceId);
        Assert.Equal(2, agentRuntime.SendCount);
    }

    [Fact]
    public async Task Agent_selected_terminals_lists_only_live_panels_and_routes_exact_subset()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentSelectionCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles,
            connectionRuntime: new SelectiveConnectionRuntime(
                UnavailableAgentConnectionId));
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        await AwaitTerminalPanelPlansAsync(workspace);
        Assert.Empty(viewModel.AgentTerminalSelectionOptions);
        ObserveTerminalPanelsActive(workspace);

        var terminals = workspace.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .ToArray();
        var unavailable = Assert.Single(
            terminals,
            terminal => terminal.ConnectionId == UnavailableAgentConnectionId);
        Assert.Equal(ConnectionPanelState.Failed, unavailable.ConnectionState);
        Assert.Null(unavailable.SessionRequest);
        Assert.Equal(3, viewModel.AgentTerminalSelectionOptions.Count);
        Assert.DoesNotContain(
            viewModel.AgentTerminalSelectionOptions,
            option => option.PanelId == unavailable.Id);
        Assert.All(
            viewModel.AgentTerminalSelectionOptions,
            option =>
            {
                var tab = Assert.Single(
                    workspace.Tabs,
                    candidate => candidate.Id == option.TabId);
                var terminal = Assert.IsType<TerminalRuntimePanelViewModel>(
                    Assert.Single(
                        tab.Panels,
                        candidate => candidate.Id == option.PanelId));
                Assert.Equal(ConnectionPanelState.Ready, terminal.ConnectionState);
                Assert.NotNull(terminal.SessionRequest);
            });

        var first = viewModel.AgentTerminalSelectionOptions[0];
        var omitted = viewModel.AgentTerminalSelectionOptions[1];
        var last = viewModel.AgentTerminalSelectionOptions[2];
        first.IsSelected = true;
        last.IsSelected = true;
        Assert.False(omitted.IsSelected);
        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.SelectedPanels);
        viewModel.AgentChat!.Prompt = "Inspect the selected terminals.";

        await viewModel.SendAgentPromptAsync();

        var target = Assert.IsType<AgentTarget.SelectedPanels>(
            agentRuntime.LastRequest!.Target);
        Assert.Equal(
            new AgentTarget.SelectedPanels(
            [
                new AgentTarget.Panel(
                    viewModel.WindowId,
                    workspace.Id,
                    first.TabId,
                    first.PanelId),
                new AgentTarget.Panel(
                    viewModel.WindowId,
                    workspace.Id,
                    last.TabId,
                    last.PanelId),
            ]),
            target);
    }

    [Fact]
    public async Task Agent_selected_terminals_refuses_a_sixty_fifth_choice()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        var snapshot = CreateAgentSelectionLimitCatalogSnapshot();
        Assert.True(LayoutValidator.Validate(snapshot.Layouts[0].Value).IsValid);
        Assert.True(WorkspaceValidator.Validate(snapshot.Workspaces[0].Value).IsValid);
        using var viewModel = CreateViewModel(
            client,
            snapshot,
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        await AwaitTerminalPanelPlansAsync(workspace);
        Assert.Empty(viewModel.AgentTerminalSelectionOptions);

        ObserveTerminalPanelsActive(workspace);

        Assert.Equal(
            AgentTarget.SelectedPanels.MaximumPanelCount + 1,
            viewModel.AgentTerminalSelectionOptions.Count);
        var validChoices = viewModel.AgentTerminalSelectionOptions
            .Take(AgentTarget.SelectedPanels.MaximumPanelCount)
            .ToArray();
        foreach (var choice in validChoices)
        {
            choice.IsSelected = true;
        }

        var validSelection = validChoices
            .Select(choice => (choice.TabId, choice.PanelId))
            .ToHashSet();
        var sixtyFifth = viewModel.AgentTerminalSelectionOptions[^1];
        sixtyFifth.IsSelected = true;

        Assert.False(sixtyFifth.IsSelected);
        Assert.Equal(
            AgentTarget.SelectedPanels.MaximumPanelCount,
            viewModel.AgentSelectedTerminalCount);
        Assert.Equal(
            validSelection,
            viewModel.AgentTerminalSelectionOptions
                .Where(choice => choice.IsSelected)
                .Select(choice => (choice.TabId, choice.PanelId))
                .ToHashSet());
        Assert.True(viewModel.HasAgentTerminalSelectionError);
        Assert.Equal(
            $"Select no more than {AgentTarget.SelectedPanels.MaximumPanelCount} terminals.",
            viewModel.AgentTerminalSelectionStatus);
    }

    [Fact]
    public async Task Agent_selected_terminal_choice_survives_tab_reorder_by_identity()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentSelectionCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        await ActivateTerminalPanelsAsync(workspace);
        var selected = viewModel.AgentTerminalSelectionOptions[0];
        selected.IsSelected = true;
        var anchor = workspace.Tabs.Last(tab => tab.Id != selected.TabId);

        Assert.True(await viewModel.MoveTabAsync(
            selected.TabId,
            anchor.Id,
            RuntimeTabPlacement.After));

        var preserved = Assert.Single(
            viewModel.AgentTerminalSelectionOptions,
            option => option.TabId == selected.TabId
                && option.PanelId == selected.PanelId);
        Assert.True(preserved.IsSelected);
        Assert.Equal(1, viewModel.AgentSelectedTerminalCount);
    }

    [Fact]
    public async Task Agent_selected_terminals_empty_selection_fails_closed_before_runtime()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentSelectionCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        await ActivateTerminalPanelsAsync(
            Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace));
        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.SelectedPanels);
        viewModel.AgentChat!.Prompt = "Do not broaden this empty scope.";

        await viewModel.SendAgentPromptAsync();

        Assert.Equal(0, agentRuntime.SendCount);
        Assert.Null(agentRuntime.LastRequest);
        Assert.Equal(
            "Select at least one live terminal before sending.",
            viewModel.AgentChat.Status);
        Assert.True(viewModel.HasAgentTerminalSelectionError);
    }

    [Fact]
    public async Task Agent_selected_terminals_session_loss_blocks_until_explicit_reselection()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentSelectionCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        await ActivateTerminalPanelsAsync(workspace);
        var lost = viewModel.AgentTerminalSelectionOptions[0];
        var retained = viewModel.AgentTerminalSelectionOptions[^1];
        lost.IsSelected = true;
        retained.IsSelected = true;
        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.SelectedPanels);

        var lostTerminal = Assert.IsType<TerminalRuntimePanelViewModel>(
            Assert.Single(
                Assert.Single(
                    workspace.Tabs,
                    tab => tab.Id == lost.TabId).Panels,
                panel => panel.Id == lost.PanelId));
        lostTerminal.ObserveSessionSnapshot(
            ClosedSessionSnapshot(
                Assert.IsType<EnsureTerminalSessionRequest>(
                    lostTerminal.SessionRequest)));
        var retainedAfterLoss = Assert.Single(
            viewModel.AgentTerminalSelectionOptions,
            option => option.PanelId == retained.PanelId);
        Assert.True(retainedAfterLoss.IsSelected);
        Assert.True(viewModel.HasAgentTerminalSelectionError);
        viewModel.AgentChat!.Prompt = "Inspect only the remaining selection.";

        await viewModel.SendAgentPromptAsync();

        Assert.Equal(0, agentRuntime.SendCount);
        Assert.Equal(
            "A selected terminal is no longer live. Review the selected terminals before sending.",
            viewModel.AgentChat.Status);

        retainedAfterLoss.IsSelected = false;
        retainedAfterLoss.IsSelected = true;
        await viewModel.SendAgentPromptAsync();

        Assert.Equal(1, agentRuntime.SendCount);
        Assert.Equal(
            new AgentTarget.SelectedPanels(
            [
                new AgentTarget.Panel(
                    viewModel.WindowId,
                    workspace.Id,
                    retainedAfterLoss.TabId,
                    retainedAfterLoss.PanelId),
            ]),
            agentRuntime.LastRequest!.Target);
    }

    [Fact]
    public async Task Agent_selected_terminal_choices_lock_while_a_run_is_bound()
    {
        var provider = CreateAgentProvider();
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateAgentSelectionCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            viewModel.RuntimeWorkspace);
        await ActivateTerminalPanelsAsync(workspace);
        var selected = viewModel.AgentTerminalSelectionOptions[0];
        selected.IsSelected = true;
        var selectedPanelsScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.SelectedPanels);
        viewModel.SelectedAgentRunScope = selectedPanelsScope;
        var boundTarget = new AgentTarget.SelectedPanels(
        [
            new AgentTarget.Panel(
                viewModel.WindowId,
                workspace.Id,
                selected.TabId,
                selected.PanelId),
        ]);

        agentRuntime.SetSnapshot(agentRuntime.Snapshot with
        {
            RunId = AgentRunId.New(),
            ProviderId = provider.Id,
            Target = boundTarget,
            TargetTitle = "Selected terminals",
            Status = "Run bound.",
        });
        await WaitForAsync(() => viewModel.AgentChat is { CanChangeProvider: false });
        selected.IsSelected = false;
        viewModel.SelectedAgentRunScope = Assert.Single(
            viewModel.AgentRunScopeOptions,
            option => option.Kind == AgentRunScopeKind.Workspace);

        Assert.True(selected.IsSelected);
        Assert.Equal(1, viewModel.AgentSelectedTerminalCount);
        Assert.Equal(selectedPanelsScope, viewModel.SelectedAgentRunScope);
    }

    [Fact]
    public async Task Agent_send_rejects_an_active_panel_that_is_not_agent_capable()
    {
        var provider = new AiProviderProfileDescriptor(
            new AiProviderProfileId("agent-provider"),
            "Agent provider",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://provider.example.test/v1/"),
            "model",
            Order: 0,
            IsEnabled: true,
            RequiresCredential: false);
        using var agentRuntime = new RecordingGovernedAgentRuntime();
        using var aiProfiles = new FixedAiProfileRuntime([provider]);
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateCatalogSnapshot(),
            agentRuntime: agentRuntime,
            aiProfiles: aiProfiles);
        Assert.True(await viewModel.OpenWorkspaceAsync(WorkspaceId));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var nonTerminalTab = workspace.Tabs[1];
        Assert.DoesNotContain(
            nonTerminalTab.Panels,
            panel => panel.Kind == PanelKind.Terminal);
        Assert.True(await viewModel.ActivateTabAsync(nonTerminalTab.Id));
        viewModel.AgentChat!.Prompt = "Try to target a non-agent panel.";

        await viewModel.SendAgentPromptAsync();

        Assert.Equal(0, agentRuntime.SendCount);
        Assert.Equal("Try to target a non-agent panel.", viewModel.AgentChat.Prompt);
        Assert.Equal(
            "Select an active terminal, browser, File Viewer, or hosted "
            + "Process Monitor panel, or choose a broader agent scope.",
            viewModel.AgentChat.Status);
    }

    [Fact]
    public void Agent_principal_requires_a_local_human_client_identity()
    {
        var principal = new FixedApprovalPrincipal(
            new ActorDescriptor(
                new ActorId("agent"),
                ActorKind.Agent,
                "Untrusted agent"));
        var (client, _) = CreateSessionClient();

        var error = Assert.Throws<ArgumentException>(() =>
            CreateViewModel(
                client,
                CreateCatalogSnapshot(),
                approvalPrincipal: principal));

        Assert.Contains("local human client identity", error.Message);
    }

    private static readonly WorkspaceId WorkspaceId = new("runtime-graph-workspace");

    /// <summary>A second workspace, for the cases about switching between them.</summary>
    private static readonly WorkspaceId SecondWorkspaceId = new("runtime-graph-workspace-2");
    private static readonly ConnectionId AppendedConnectionId =
        new("runtime-graph-secondary");
    private static readonly ScreenId AppendedScreenId =
        new("runtime-graph-screen");
    private static readonly ConnectionId UnavailableAgentConnectionId =
        new("agent-unavailable");

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The asynchronous runtime-graph condition was not observed.");
    }

    private static async Task ActivateTerminalPanelsAsync(
        RuntimeWorkspaceViewModel workspace)
    {
        await AwaitTerminalPanelPlansAsync(workspace);
        ObserveTerminalPanelsActive(workspace);
    }

    private static async Task AwaitTerminalPanelPlansAsync(
        RuntimeWorkspaceViewModel workspace) =>
        await Task.WhenAll(workspace.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .Select(panel => panel.Initialization));

    private static void ObserveTerminalPanelsActive(
        RuntimeWorkspaceViewModel workspace)
    {
        foreach (var terminal in workspace.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>())
        {
            if (terminal.SessionRequest is { } request)
            {
                terminal.ObserveSessionSnapshot(ActiveSessionSnapshot(request));
            }
        }
    }

    private static AiProviderProfileDescriptor CreateAgentProvider() =>
        new(
            new AiProviderProfileId("agent-provider"),
            "Agent provider",
            AiProviderKind.OpenAiCompatible,
            new Uri("https://provider.example.test/v1/"),
            "model",
            Order: 0,
            IsEnabled: true,
            RequiresCredential: false);

    private static SessionSnapshot ClosedSessionSnapshot(
        EnsureTerminalSessionRequest request) =>
        SessionSnapshot(
            request,
            SessionLifecycle.Closed,
            SessionHealth.Ended,
            "Session closed.");

    private static SessionSnapshot ActiveSessionSnapshot(
        EnsureTerminalSessionRequest request) =>
        SessionSnapshot(
            request,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            "Session active.");

    private static SessionSnapshot SessionSnapshot(
        EnsureTerminalSessionRequest request,
        SessionLifecycle lifecycle,
        SessionHealth health,
        string statusDetail) =>
        new(
            new SessionDescriptor(
                request.SessionId,
                PanelKind.Terminal,
                lifecycle,
                health,
                request.Owner,
                CapabilitySet.Empty,
                Revision: 2,
                HasActiveWork: false,
                statusDetail),
            LastSequence: 2,
            Attachments: [],
            InputLease: null);

    private static (ISessionHostClient Client, RecordingSessionClient Recorder)
        CreateSessionClient()
    {
        var client = DispatchProxy.Create<ISessionHostClient, RecordingSessionClient>();
        return (client, (RecordingSessionClient)(object)client);
    }

    /// <summary>
    /// Home is a summary. Without a bound preview a profile with many saved
    /// definitions would push every later section off the page, and the "View
    /// all" link would have nothing left to reveal.
    /// </summary>
    [Fact]
    public void Home_previews_are_bounded_while_the_dedicated_pages_show_everything()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateFixedCatalog(CreateManyConnectionsSnapshot(connectionCount: 12)));

        Assert.Equal(12, viewModel.Connections.Count);
        Assert.Equal(8, viewModel.ConnectionsPreview.Count);
        Assert.True(viewModel.HasMoreConnectionsThanPreview);

        // The preview is the head of the same list, not a differently ordered one.
        Assert.Equal(
            viewModel.Connections.Take(8).Select(item => item.Id),
            viewModel.ConnectionsPreview.Select(item => item.Id));
    }

    [Fact]
    public void A_short_connection_list_is_shown_whole_on_home()
    {
        var (client, _) = CreateSessionClient();
        using var viewModel = CreateViewModel(
            client,
            CreateFixedCatalog(CreateManyConnectionsSnapshot(connectionCount: 3)));

        Assert.Equal(3, viewModel.ConnectionsPreview.Count);
        Assert.False(viewModel.HasMoreConnectionsThanPreview);
    }

    private static DefinitionCatalogSnapshot CreateManyConnectionsSnapshot(int connectionCount)
    {
        var connections = Enumerable.Range(0, connectionCount)
            .Select(index => new StoredDefinition<ConnectionProfile>(
                new ConnectionProfile(
                    new ConnectionId($"connection-{index:00}"),
                    ConnectionProfile.CurrentSchemaVersion,
                    $"connection-{index:00}",
                    new ConnectionEndpoint.Local("/bin/sh"),
                    new ConnectionAuthentication.None(),
                    ConnectionStartup.Default,
                    ConnectionKeepAlive.Disabled,
                    SshHostKeyPolicy.NotApplicable,
                    []),
                1,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch))
            .ToArray();

        return CreateCatalogSnapshot() with { Connections = connections };
    }

    private static int VisibleRouteCount(MainWindowViewModel viewModel) =>
        new[]
        {
            viewModel.IsWorkspaceVisible,
            viewModel.IsSettingsVisible,
        }.Count(isVisible => isVisible);

    private static int VisibleSettingsPageCount(MainWindowViewModel viewModel) =>
        new[]
        {
            viewModel.IsAppearanceSettingsVisible,
            viewModel.IsWorkspaceSettingsVisible,
            viewModel.IsKeybindingSettingsVisible,
            viewModel.IsFilesSettingsVisible,
            viewModel.IsTerminalSettingsVisible,
            viewModel.IsQuickTerminalSettingsVisible,
            viewModel.IsSecretsSettingsVisible,
            viewModel.IsDiagnosticsSettingsVisible,
            viewModel.IsAgentSettingsVisible,
            viewModel.IsMcpSettingsVisible,
            viewModel.IsAboutSettingsVisible,
        }.Count(isVisible => isVisible);

    private static int VisibleOverlayCount(MainWindowViewModel viewModel) =>
        new[]
        {
            viewModel.IsCommandPaletteVisible,
            viewModel.IsNewPanelVisible,
            viewModel.IsLayoutDesignerVisible,
            viewModel.IsDefinitionEditorVisible,
        }.Count(isVisible => isVisible);

    private static MainWindowViewModel CreateViewModel(
        ISessionHostClient sessionClient,
        DefinitionCatalogSnapshot snapshot,
        IUiThreadDispatcher? uiThreadDispatcher = null,
        IGovernedAgentRuntime? agentRuntime = null,
        IAiProviderProfileRuntime? aiProfiles = null,
        IAgentApprovalPrincipal? approvalPrincipal = null,
        IBrowserRendererViewFactory? browserRendererFactory = null,
        IConnectionRuntime? connectionRuntime = null,
        IFilePanelClient? filePanelClient = null,
        IFileTransferQueueClient? fileTransferQueueClient = null,
        IDockerEngineClient? dockerEngineClient = null) =>
        CreateViewModel(
            sessionClient,
            CreateFixedCatalog(snapshot),
            uiThreadDispatcher,
            agentRuntime,
            aiProfiles,
            approvalPrincipal,
            browserRendererFactory,
            connectionRuntime,
            filePanelClient,
            fileTransferQueueClient,
            dockerEngineClient);

    private static MainWindowViewModel CreateViewModel(
        ISessionHostClient sessionClient,
        IDefinitionCatalog catalog,
        IUiThreadDispatcher? uiThreadDispatcher = null,
        IGovernedAgentRuntime? agentRuntime = null,
        IAiProviderProfileRuntime? aiProfiles = null,
        IAgentApprovalPrincipal? approvalPrincipal = null,
        IBrowserRendererViewFactory? browserRendererFactory = null,
        IConnectionRuntime? connectionRuntime = null,
        IFilePanelClient? filePanelClient = null,
        IFileTransferQueueClient? fileTransferQueueClient = null,
        IDockerEngineClient? dockerEngineClient = null)
    {
        var files = new EmptyFileClients();
        return new MainWindowViewModel(
            sessionClient,
            catalog,
            connectionRuntime ?? new SuccessfulConnectionRuntime(),
            new EmptySecretVault(),
            filePanelClient ?? files,
            fileTransferQueueClient ?? files,
            new TerminalStartupCommandDispatcher(new SuccessfulAuditStore(), TimeProvider.System),
            uiThreadDispatcher: uiThreadDispatcher,
            aiProviderRuntime: aiProfiles,
            agentChatRuntime: agentRuntime,
            agentApprovalPrincipal: approvalPrincipal,
            browserRendererViewFactory: browserRendererFactory,
            dockerEngineClient: dockerEngineClient);
    }

    private sealed class SingleContainerDockerClient : IDockerEngineClient
    {
        private static readonly DockerContainerSummary Container = new(
            "container-api",
            "api",
            "demo/api:latest",
            "running",
            "Up 2 hours",
            "8080/tcp",
            "2 hours ago",
            "1%",
            "64 MiB",
            "—",
            "—",
            "demo",
            "api");
        private readonly DockerResult<string> _shellResult;

        public SingleContainerDockerClient(DockerResult<string>? shellResult = null)
        {
            _shellResult = shellResult
                ?? new DockerResult<string>.Success("/bin/ash");
        }

        public List<ConnectionId> ReadConnections { get; } = [];

        public ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            ReadConnections.Add(connection.Id);
            return ValueTask.FromResult<DockerResult<DockerEngineSnapshot>>(
                new DockerResult<DockerEngineSnapshot>.Success(new DockerEngineSnapshot(
                    new DockerEngineSummary("28.3.0", "linux", "arm64", "1.51"),
                    [Container],
                    [],
                    [],
                    [],
                    DateTimeOffset.UtcNow)));
        }

        public ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<IReadOnlyList<DockerVolumeUsage>>>(
                new DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success([]));

        public ValueTask<DockerResult<DockerResourceInspection>> InspectAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<DockerResourceInspection>>(
                new DockerResult<DockerResourceInspection>.Success(
                    new DockerResourceInspection(resource, [], "{}")));

        public ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
            ConnectionProfile connection,
            DockerContainerLogRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<DockerContainerLogPage>>(
                new DockerResult<DockerContainerLogPage>.Success(
                    new DockerContainerLogPage([], false, null, null)));

        public ValueTask<DockerResult<bool>> DownloadContainerLogsAsync(
            ConnectionProfile connection,
            string containerId,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<bool>>(new DockerResult<bool>.Success(true));

        public ValueTask<DockerResult<string>> ResolveContainerShellAsync(
            ConnectionProfile connection,
            string containerId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_shellResult);

        public ValueTask<DockerResult<DockerFileListing>> ListFilesAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<DockerFileListing>>(
                new DockerResult<DockerFileListing>.Success(
                    new DockerFileListing(resource, path, [])));

        public ValueTask<DockerResult<bool>> RunContainerActionAsync(
            ConnectionProfile connection,
            string containerId,
            DockerContainerAction action,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<bool>>(
                new DockerResult<bool>.Success(true));
    }

    private static IDefinitionCatalog CreateFixedCatalog(DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, FixedCatalogProxy>();
        ((FixedCatalogProxy)(object)catalog).Snapshot = snapshot;
        return catalog;
    }

    private static DefinitionCatalogSnapshot CreateCatalogSnapshot()
    {
        var layoutId = new LayoutId("runtime-graph-layout");
        var slots = new[]
        {
            new LayoutSlotDefinition(
                new LayoutSlotId("left"),
                new LayoutGridBounds(0, 0, 1, 1),
                new LayoutMinimumSize(220, 140)),
            new LayoutSlotDefinition(
                new LayoutSlotId("middle"),
                new LayoutGridBounds(1, 0, 1, 1),
                new LayoutMinimumSize(220, 140)),
            new LayoutSlotDefinition(
                new LayoutSlotId("right"),
                new LayoutGridBounds(2, 0, 1, 1),
                new LayoutMinimumSize(220, 140)),
        };
        var layout = new LayoutDefinition(
            layoutId,
            LayoutDefinition.CurrentSchemaVersion,
            "Three columns",
            new LayoutGrid(3, 1),
            slots);
        var connection = new ConnectionProfile(
            new ConnectionId("runtime-graph-local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Runtime graph",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("alpha"),
                    "Alpha",
                    layoutId,
                    [
                        Panel("alpha-terminal", "left", ScreenPanelKind.Terminal, "Terminal"),
                        Panel("alpha-browser", "middle", ScreenPanelKind.Browser, "Browser"),
                        Panel("alpha-files", "right", ScreenPanelKind.FileViewer, "Files"),
                    ]),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("beta"),
                    "Beta",
                    layoutId,
                    [Panel("beta-stats", "left", ScreenPanelKind.Statistics, "Statistics")]),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("gamma"),
                    "Gamma",
                    layoutId,
                    [Panel("gamma-process", "left", ScreenPanelKind.ProcessMonitor, "Processes")]),
            ]);

        var second = new WorkspaceDefinition(
            SecondWorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Runtime graph second",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("delta"),
                    "Delta",
                    layoutId,
                    [Panel("delta-terminal", "left", ScreenPanelKind.Terminal, "Terminal")]),
            ]);

        return new DefinitionCatalogSnapshot(
            [Store(connection)],
            [Store(layout)],
            [],
            [Store(workspace), Store(second)],
            [],
            [],
            [],
            [],
            []);
    }

    private static DefinitionCatalogSnapshot CreateTabAppendCatalogSnapshot()
    {
        var snapshot = CreateCatalogSnapshot();
        var layout = Assert.Single(snapshot.Layouts).Value;
        var connection = new ConnectionProfile(
            AppendedConnectionId,
            ConnectionProfile.CurrentSchemaVersion,
            "Secondary local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var screen = new ScreenDefinition(
            AppendedScreenId,
            ScreenDefinition.CurrentSchemaVersion,
            "Operations screen",
            "Two terminals using the saved three-column layout.",
            layout.Id,
            [
                Panel(
                    "operations-left",
                    "left",
                    ScreenPanelKind.Terminal,
                    "Operations left",
                    connection.Id),
                Panel(
                    "operations-right",
                    "right",
                    ScreenPanelKind.Terminal,
                    "Operations right",
                    connection.Id),
            ]);

        return snapshot with
        {
            Connections = snapshot.Connections.Append(Store(connection)).ToArray(),
            Screens = [Store(screen)],
        };
    }

    private static DefinitionCatalogSnapshot CreateDeferredPanelAppendCatalogSnapshot()
    {
        var layout = new LayoutDefinition(
            new LayoutId("deferred-panel-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Deferred panel layout",
            new LayoutGrid(2, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("left"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("right"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Deferred panel base",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("deferred-base-tab"),
                    "Base",
                    layout.Id,
                    [Panel("deferred-base-browser", "left", ScreenPanelKind.Browser, "Browser")]),
            ]);
        var screen = new ScreenDefinition(
            AppendedScreenId,
            ScreenDefinition.CurrentSchemaVersion,
            "Deferred panels",
            "A File Viewer and local statistics panel.",
            layout.Id,
            [
                Panel("deferred-files", "left", ScreenPanelKind.FileViewer, "Files"),
                Panel("deferred-stats", "right", ScreenPanelKind.Statistics, "Statistics"),
            ]);
        return new DefinitionCatalogSnapshot(
            [],
            [Store(layout)],
            [Store(screen)],
            [Store(workspace)],
            [],
            [],
            [],
            [],
            []);
    }

    private static DefinitionCatalogSnapshot CreateAgentSelectionCatalogSnapshot()
    {
        var layoutId = new LayoutId("agent-selection-layout");
        var layout = new LayoutDefinition(
            layoutId,
            LayoutDefinition.CurrentSchemaVersion,
            "Agent selection",
            new LayoutGrid(3, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("left"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("middle"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("right"),
                    new LayoutGridBounds(2, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var liveConnection = new ConnectionProfile(
            new ConnectionId("agent-live"),
            ConnectionProfile.CurrentSchemaVersion,
            "Live local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var unavailableConnection = new ConnectionProfile(
            UnavailableAgentConnectionId,
            ConnectionProfile.CurrentSchemaVersion,
            "Unavailable local",
            new ConnectionEndpoint.Local("/missing/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Agent selection",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("agent-alpha"),
                    "Alpha",
                    layoutId,
                    [
                        Panel(
                            "agent-alpha-terminal",
                            "left",
                            ScreenPanelKind.Terminal,
                            "Alpha terminal",
                            liveConnection.Id),
                        Panel(
                            "agent-alpha-browser",
                            "middle",
                            ScreenPanelKind.Browser,
                            "Alpha browser"),
                        Panel(
                            "agent-alpha-unavailable",
                            "right",
                            ScreenPanelKind.Terminal,
                            "Unavailable terminal",
                            unavailableConnection.Id),
                    ]),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("agent-beta"),
                    "Beta",
                    layoutId,
                    [
                        Panel(
                            "agent-beta-terminal",
                            "left",
                            ScreenPanelKind.Terminal,
                            "Beta terminal",
                            liveConnection.Id),
                        Panel(
                            "agent-beta-stats",
                            "middle",
                            ScreenPanelKind.Statistics,
                            "Beta statistics"),
                    ]),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("agent-gamma"),
                    "Gamma",
                    layoutId,
                    [
                        Panel(
                            "agent-gamma-terminal",
                            "left",
                            ScreenPanelKind.Terminal,
                            "Gamma terminal",
                            liveConnection.Id),
                    ]),
            ]);

        return new DefinitionCatalogSnapshot(
            [Store(liveConnection), Store(unavailableConnection)],
            [Store(layout)],
            [],
            [Store(workspace)],
            [],
            [],
            [],
            [],
            []);
    }

    private static DefinitionCatalogSnapshot CreateAgentSelectionLimitCatalogSnapshot()
    {
        var layout = new LayoutDefinition(
            new LayoutId("agent-selection-limit-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Single terminal",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var connection = new ConnectionProfile(
            new ConnectionId("agent-selection-limit-local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var entries = Enumerable
            .Range(1, AgentTarget.SelectedPanels.MaximumPanelCount + 1)
            .Select(index => (WorkspaceEntry)new WorkspaceEntry.Tab(
                new WorkspaceEntryId($"agent-limit-tab-{index:D2}"),
                $"Terminal {index:D2}",
                layout.Id,
                [
                    Panel(
                        $"agent-limit-panel-{index:D2}",
                        "main",
                        ScreenPanelKind.Terminal,
                        $"Terminal {index:D2}",
                        connection.Id),
                ]))
            .ToArray();
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Agent selection limit",
            null,
            null,
            entries);

        return new DefinitionCatalogSnapshot(
            [Store(connection)],
            [Store(layout)],
            [],
            [Store(workspace)],
            [],
            [],
            [],
            [],
            []);
    }

    private static DefinitionCatalogSnapshot CreateSinglePanelCatalogSnapshot()
    {
        var layoutId = new LayoutId("single-panel-layout");
        var layout = new LayoutDefinition(
            layoutId,
            LayoutDefinition.CurrentSchemaVersion,
            "Single panel",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Runtime graph",
            null,
            null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("only-tab"),
                    "Only tab",
                    layoutId,
                    [Panel("only-panel", "main", ScreenPanelKind.Browser, "Browser")]),
            ]);
        return new DefinitionCatalogSnapshot(
            [],
            [Store(layout)],
            [],
            [Store(workspace)],
            [],
            [],
            [],
            [],
            []);
    }

    private static DefinitionCatalogSnapshot CreateAgentPolicyCatalogSnapshot(
        AgentPolicy? firstPolicy,
        AgentPolicy? secondPolicy)
    {
        var layout = new LayoutDefinition(
            new LayoutId("agent-policy-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Agent policy layout",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var connection = new ConnectionProfile(
            new ConnectionId("agent-policy-local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Agent policy local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var firstScreen = new ScreenDefinition(
            new ScreenId("agent-policy-first"),
            ScreenDefinition.CurrentSchemaVersion,
            "First policy screen",
            null,
            layout.Id,
            [
                Panel(
                    "agent-policy-first-panel",
                    "main",
                    ScreenPanelKind.Terminal,
                    "First terminal",
                    connection.Id),
            ],
            agentPolicyOverride: firstPolicy);
        var secondScreen = new ScreenDefinition(
            new ScreenId("agent-policy-second"),
            ScreenDefinition.CurrentSchemaVersion,
            "Second policy screen",
            null,
            layout.Id,
            [
                Panel(
                    "agent-policy-second-panel",
                    "main",
                    ScreenPanelKind.Terminal,
                    "Second terminal",
                    connection.Id),
            ],
            agentPolicyOverride: secondPolicy);
        var workspace = new WorkspaceDefinition(
            WorkspaceId,
            WorkspaceDefinition.CurrentSchemaVersion,
            "Agent policy workspace",
            null,
            null,
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("agent-policy-first-entry"),
                    firstScreen.Id,
                    null),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("agent-policy-second-entry"),
                    secondScreen.Id,
                    null),
            ]);
        return new DefinitionCatalogSnapshot(
            [Store(connection, revision: 2)],
            [Store(layout, revision: 4)],
            [
                Store(firstScreen, revision: 5),
                Store(secondScreen, revision: 6),
            ],
            [Store(workspace, revision: 3)],
            [],
            [],
            [],
            [],
            []);
    }

    private static AgentPolicy Policy(
        string provider,
        string model,
        Func<AgentCapability, AgentPermission> permission) =>
        new(
            provider,
            model,
            AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                permission));

    private static void AssertPolicyEqual(AgentPolicy expected, AgentPolicy actual)
    {
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.Model, actual.Model);
        Assert.All(
            AgentPolicy.Capabilities,
            capability => Assert.Equal(
                expected.GetPermission(capability),
                actual.GetPermission(capability)));
    }

    private static ScreenPanelDefinition Panel(
        string id,
        string slot,
        ScreenPanelKind kind,
        string title,
        ConnectionId? connectionId = null) => new(
            new ScreenPanelId(id),
            new LayoutSlotId(slot),
            kind,
            title,
            connectionId,
            PanelStartupBehavior.None);

    private static StoredDefinition<T> Store<T>(T definition, long revision = 1)
        where T : IDurableDefinition =>
        new(definition, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static FileProviderProfileDescriptor FileProfile(
        FileProviderProfileId id,
        string name,
        FileProviderFamily family,
        string authority)
    {
        var root = new FilePanelLocation(
            id.Value,
            authority,
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        return new FileProviderProfileDescriptor(
            id.Value,
            name,
            family,
            root,
            FilePanelCapability.List,
            500,
            1024 * 1024);
    }

    private sealed class RecordingBrowserRendererViewFactory :
        IBrowserRendererViewFactory
    {
        private readonly List<RecordingBrowserRendererLifetime> _lifetimes = [];

        public List<ConnectionId> CreatedConnections { get; } = [];

        public int CreateCount { get; private set; }

        public int DisposeCount => _lifetimes.Sum(lifetime => lifetime.DisposeCount);

        public BrowserRendererView Create()
        {
            CreateCount++;
            var lifetime = new RecordingBrowserRendererLifetime();
            _lifetimes.Add(lifetime);
            return new BrowserRendererView(
                new Border(),
                new RecordingBrowserRenderer(),
                lifetime);
        }

        public ValueTask<BrowserRendererView> CreateAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedConnections.Add(connection.Id);
            return ValueTask.FromResult(Create());
        }
    }

    private sealed class RecordingBrowserRendererLifetime : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingBrowserRenderer : IBrowserRenderer
    {
        public BrowserSessionState State { get; private set; } =
            BrowserSessionState.Initial(BrowserAddress.Blank);

        public CapabilitySet Capabilities { get; } = new(
        [
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserOriginGuard,
        ]);

        public event EventHandler<BrowserStateChangedEventArgs>? StateChanged;

        public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
            BrowserAddress address,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = new BrowserSessionState(
                address,
                string.Empty,
                BrowserLoadState.Ready,
                false,
                false,
                State.DocumentRevision + 1);
            StateChanged?.Invoke(this, new BrowserStateChangedEventArgs(State));
            return Success();
        }

        public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
            CancellationToken cancellationToken) =>
            Unchanged(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
            CancellationToken cancellationToken) =>
            Unchanged(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
            CancellationToken cancellationToken) =>
            Unchanged(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
            CancellationToken cancellationToken) =>
            Unchanged(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>>
            NavigateWithinOriginAsync(
                BrowserOriginConstrainedNavigationRequest request,
                BrowserNavigationOrigin allowedOrigin,
                BrowserNavigationStartBinding startBinding,
                CancellationToken cancellationToken) =>
            request switch
            {
                BrowserOriginConstrainedNavigationRequest.Navigate navigate =>
                    NavigateAsync(navigate.Address, cancellationToken),
                _ => Unchanged(cancellationToken),
            };

        public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
            CaptureSnapshotAsync(
                BrowserDocumentBinding document,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Success(
                    new BrowserDocumentSnapshot(
                        document,
                        [new BrowserSnapshotNode(
                            0,
                            "document",
                            string.Empty)],
                        DateTimeOffset.UnixEpoch)));
        }

        public ValueTask<BrowserResult<BrowserClickReceipt>>
            ClickWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Success(
                    new BrowserClickReceipt(reference.Document)));
        }

        public ValueTask<BrowserResult<BrowserFillReceipt>>
            FillWithinOriginAsync(
                BrowserElementReference reference,
                string text,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Success(
                    new BrowserFillReceipt(reference.Document)));
        }

        public ValueTask<BrowserResult<BrowserCheckReceipt>>
            CheckWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Success(
                    new BrowserCheckReceipt(reference.Document)));
        }

        private ValueTask<BrowserResult<BrowserSessionState>> Unchanged(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Success();
        }

        private ValueTask<BrowserResult<BrowserSessionState>> Success() =>
            ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Success(State));
    }

    public sealed record WorkspaceRegistration(
        RegisterWorkspaceGraphRequest Request,
        OperationContext Context);

    public sealed record TabActivation(
        ActivateWorkspaceTabRequest Request,
        OperationContext Context);

    public sealed record WorkspaceUnregistration(
        UnregisterWorkspaceGraphRequest Request,
        OperationContext Context);

    public class RecordingSessionClient : DispatchProxy
    {
        private readonly object _gate = new();
        private readonly List<WorkspaceRegistration> _registrations = [];
        private readonly List<TabActivation> _tabActivations = [];
        private readonly List<WorkspaceUnregistration> _unregistrations = [];
        private readonly Dictionary<WorkspaceInstanceId, Channel<WorkspaceGraphStreamItem>>
            _workspaceEvents = [];
        private WorkspaceGraphSnapshot? _workspace;
        private int _activeTabActivations;
        private int _maximumConcurrentTabActivations;
        private int _activeWatchCount;
        private int _watchStartCount;
        private int _filePanelEnsureCount;
        private int _statisticsEnsureCount;

        public bool RejectNextTabActivation { get; set; }

        public bool RejectNextRegistration { get; set; }

        public bool DelayNextRegistration { get; set; }

        public bool AcceptThenCancelNextRegistration { get; set; }

        public bool FailNextRegistrationWithTransportError { get; set; }

        public bool AcceptThenCancelNextUnregistration { get; set; }

        public bool DelayFirstTabActivation { get; set; }

        public bool FailWatchWhenCancelled { get; set; }

        public bool StallNextWorkspaceQuery { get; set; }

        public bool AcceptProcessMonitorSessions { get; set; }

        public bool WorkspaceQueryTokenWasCancellationRequestedOnEntry { get; private set; }

        public SessionId? NextRegistrationSessionId { get; set; }

        public Func<
            WorkspaceGraphSnapshot,
            HostResult<WorkspaceGraphSnapshot>>? NextRegistrationReceiptFactory
        { get; set; }

        public Func<
            WorkspaceGraphSnapshot,
            HostResult<WorkspaceGraphSnapshot>>? NextTabActivationReceiptFactory
        { get; set; }

        public Func<
            WorkspaceGraphSnapshot,
            HostResult<WorkspaceGraphSnapshot>>? NextPanelActivationReceiptFactory
        { get; set; }

        public TaskCompletionSource FirstTabActivationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFirstTabActivation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DelayedRegistrationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowDelayedRegistration { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WorkspaceQueryEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WatchStopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<WorkspaceRegistration> Registrations
        {
            get
            {
                lock (_gate)
                {
                    return _registrations.ToArray();
                }
            }
        }

        public IReadOnlyList<TabActivation> TabActivations
        {
            get
            {
                lock (_gate)
                {
                    return _tabActivations.ToArray();
                }
            }
        }

        public IReadOnlyList<WorkspaceUnregistration> Unregistrations
        {
            get
            {
                lock (_gate)
                {
                    return _unregistrations.ToArray();
                }
            }
        }

        public int MaximumConcurrentTabActivations =>
            Volatile.Read(ref _maximumConcurrentTabActivations);

        public int ActiveWatchCount => Volatile.Read(ref _activeWatchCount);

        public int WatchStartCount => Volatile.Read(ref _watchStartCount);

        public int FilePanelEnsureCount => Volatile.Read(ref _filePanelEnsureCount);

        public int StatisticsEnsureCount => Volatile.Read(ref _statisticsEnsureCount);

        public WorkspaceGraphSnapshot? CurrentWorkspace
        {
            get
            {
                lock (_gate)
                {
                    return _workspace;
                }
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(ISessionHostClient.RegisterWorkspaceGraphAsync)
                    when args is [RegisterWorkspaceGraphRequest request, OperationContext context, CancellationToken cancellationToken] =>
                    RegisterWorkspaceAsync(request, context, cancellationToken),
                nameof(ISessionHostClient.UnregisterWorkspaceGraphAsync)
                    when args is [UnregisterWorkspaceGraphRequest request, OperationContext context, CancellationToken cancellationToken] =>
                    UnregisterWorkspace(request, context, cancellationToken),
                nameof(ISessionHostClient.GetWorkspaceGraphAsync)
                    when args is [WorkspaceInstanceId workspaceId, OperationContext, CancellationToken cancellationToken] =>
                    GetWorkspace(workspaceId, cancellationToken),
                nameof(ISessionHostClient.WatchWorkspaceGraphAsync)
                    when args is [WatchWorkspaceGraphRequest request, .., CancellationToken cancellationToken] =>
                    WatchWorkspace(request, cancellationToken),
                nameof(ISessionHostClient.ActivateWorkspaceTabAsync)
                    when args is [ActivateWorkspaceTabRequest request, OperationContext context, CancellationToken cancellationToken] =>
                    ActivateTabAsync(request, context, cancellationToken),
                nameof(ISessionHostClient.ActivateWorkspacePanelAsync)
                    when args is [ActivateWorkspacePanelRequest request, OperationContext context, ..] =>
                    ActivatePanel(request, context),
                nameof(ISessionHostClient.EnsureStatisticsSessionAsync)
                    when args is [EnsureStatisticsSessionRequest, ..] =>
                    RejectStatisticsSession(),
                nameof(ISessionHostClient.EnsureProcessMonitorSessionAsync)
                    when args is
                    [
                        EnsureProcessMonitorSessionRequest request,
                        ..,
                        CancellationToken cancellationToken,
                    ] =>
                    ResolveProcessMonitorSession(request, cancellationToken),
                nameof(ISessionHostClient.ListProcessesAsync)
                    when args is
                    [
                        ProcessMonitorHostRequest,
                        ..,
                        CancellationToken cancellationToken,
                    ] =>
                    ResolveProcessList(cancellationToken),
                nameof(ISessionHostClient.EnsureFilePanelSessionAsync)
                    when args is [EnsureFilePanelSessionRequest, ..] =>
                    RejectFilePanelSession(),
                nameof(ISessionHostClient.CloseAsync)
                    when args is [CloseScopeRequest request, ..] =>
                    ValueTask.FromResult(HostResult<CloseScopeResult>.Succeed(
                        new CloseScopeResult.Completed(
                            request.Scope,
                            request.TargetId,
                            []),
                        1)),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        public void LinkFirstPanelSession(SessionId sessionId, bool publishEvent)
        {
            WorkspaceGraphEvent workspaceEvent;
            Channel<WorkspaceGraphStreamItem> channel;
            lock (_gate)
            {
                var current = _workspace
                    ?? throw new InvalidOperationException("A workspace must be registered first.");
                var firstTab = current.Workspace.Tabs[0];
                var firstPanel = firstTab.Panels[0];
                var linkedWorkspace = ReplaceFirstPanelSession(
                    current.Workspace,
                    sessionId);
                var revision = current.Revision + 1;
                var sequence = current.LastSequence + 1;
                _workspace = new WorkspaceGraphSnapshot(
                    current.WindowId,
                    linkedWorkspace,
                    revision,
                    sequence);
                workspaceEvent = new WorkspaceGraphEvent(
                    current.WindowId,
                    linkedWorkspace,
                    sequence,
                    revision,
                    WorkspaceGraphEventKind.PanelSessionLinked,
                    DateTimeOffset.UtcNow,
                    firstTab.Id,
                    firstPanel.Id,
                    sessionId);
                channel = WorkspaceEvents(current.Workspace.Id);
            }

            if (publishEvent)
            {
                Assert.True(channel.Writer.TryWrite(
                    new WorkspaceGraphStreamItem.Event(workspaceEvent)));
            }
        }

        public void PublishResynchronization()
        {
            WorkspaceGraphSnapshot current;
            Channel<WorkspaceGraphStreamItem> channel;
            lock (_gate)
            {
                current = _workspace
                    ?? throw new InvalidOperationException("A workspace must be registered first.");
                channel = WorkspaceEvents(current.Workspace.Id);
            }

            Assert.True(channel.Writer.TryWrite(
                new WorkspaceGraphStreamItem.ResynchronizationRequired(
                    current,
                    current.LastSequence)));
        }

        private async ValueTask<HostResult<WorkspaceGraphSnapshot>> RegisterWorkspaceAsync(
            RegisterWorkspaceGraphRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            bool reject;
            bool delay;
            bool acceptThenCancel;
            bool failWithTransportError;
            lock (_gate)
            {
                _registrations.Add(new WorkspaceRegistration(request, context));
                reject = RejectNextRegistration;
                RejectNextRegistration = false;
                delay = DelayNextRegistration;
                DelayNextRegistration = false;
                acceptThenCancel = AcceptThenCancelNextRegistration;
                AcceptThenCancelNextRegistration = false;
                failWithTransportError = FailNextRegistrationWithTransportError;
                FailNextRegistrationWithTransportError = false;
            }

            if (delay)
            {
                DelayedRegistrationEntered.TrySetResult();
                await AllowDelayedRegistration.Task.WaitAsync(cancellationToken);
            }

            if (failWithTransportError)
            {
                throw new IOException("The test transport lost the registration request.");
            }

            lock (_gate)
            {
                var currentRevision = _workspace?.Workspace.Id == request.Workspace.Id
                    ? _workspace.Revision
                    : 0;
                if (reject || context.ExpectedRevision is { } expectedRevision
                    && expectedRevision != currentRevision)
                {
                    return HostResult<WorkspaceGraphSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.RevisionConflict,
                            "The test host rejected the workspace proposal."),
                        currentRevision);
                }

                var registeredWorkspace = NextRegistrationSessionId is { } sessionId
                    ? ReplaceFirstPanelSession(request.Workspace, sessionId)
                    : request.Workspace;
                NextRegistrationSessionId = null;
                var revision = currentRevision + 1;
                _workspace = new WorkspaceGraphSnapshot(
                    request.WindowId,
                    registeredWorkspace,
                    revision,
                    revision);
                _ = WorkspaceEvents(request.Workspace.Id);
                if (acceptThenCancel)
                {
                    throw new OperationCanceledException(
                        "The test host accepted the graph but lost the response.",
                        cancellationToken);
                }

                var receiptFactory = NextRegistrationReceiptFactory;
                NextRegistrationReceiptFactory = null;
                return receiptFactory?.Invoke(_workspace)
                    ?? HostResult<WorkspaceGraphSnapshot>.Succeed(_workspace, revision);
            }
        }

        private async ValueTask<HostResult<WorkspaceGraphSnapshot>> GetWorkspace(
            WorkspaceInstanceId workspaceId,
            CancellationToken cancellationToken)
        {
            bool stall;
            lock (_gate)
            {
                stall = StallNextWorkspaceQuery;
                StallNextWorkspaceQuery = false;
                if (stall)
                {
                    WorkspaceQueryTokenWasCancellationRequestedOnEntry =
                        cancellationToken.IsCancellationRequested;
                }
            }

            if (stall)
            {
                WorkspaceQueryEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            lock (_gate)
            {
                if (_workspace is not { } current
                    || current.Workspace.Id != workspaceId)
                {
                    return HostResult<WorkspaceGraphSnapshot>.Fail(
                        HostError.Create(HostErrorCode.NotFound, "The workspace was not found."),
                        0);
                }

                return HostResult<WorkspaceGraphSnapshot>.Succeed(
                    current,
                    current.Revision);
            }
        }

        private ValueTask<HostResult<SessionSnapshot>> RejectStatisticsSession()
        {
            Interlocked.Increment(ref _statisticsEnsureCount);
            return ValueTask.FromResult(HostResult<SessionSnapshot>.Fail(
                HostError.Create(
                    HostErrorCode.CapabilityNotSupported,
                    "The test host does not provide statistics samples."),
                0));
        }

        private ValueTask<HostResult<SessionSnapshot>> ResolveProcessMonitorSession(
            EnsureProcessMonitorSessionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AcceptProcessMonitorSessions)
            {
                return ValueTask.FromResult(HostResult<SessionSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.CapabilityNotSupported,
                        "The test host does not provide process-monitor samples."),
                    0));
            }

            var descriptor = new SessionDescriptor(
                request.SessionId,
                PanelKind.ProcessMonitor,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                request.Owner,
                new CapabilitySet(
                [
                    SessionCapabilities.AttachRead,
                    SessionCapabilities.ProcessesList,
                ]),
                Revision: 1,
                HasActiveWork: false,
                StatusDetail: "Ready");
            return ValueTask.FromResult(
                HostResult<SessionSnapshot>.Succeed(
                    new SessionSnapshot(descriptor, 1, [], null),
                    resultingRevision: 1));
        }

        private ValueTask<HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>>
            ResolveProcessList(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AcceptProcessMonitorSessions)
            {
                return ValueTask.FromResult(
                    HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>.Fail(
                        HostError.Create(
                            HostErrorCode.CapabilityNotSupported,
                            "The test host does not provide process-monitor samples."),
                        0));
            }

            return ValueTask.FromResult(
                HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>.Succeed(
                    MonitorPanelResult<ProcessMonitorSnapshot>.Success(
                        new ProcessMonitorSnapshot(
                            DateTimeOffset.UtcNow,
                            [],
                            EnumeratedProcessCount: 0,
                            ObservedProcessCount: 0,
                            IsTruncated: false)),
                    resultingRevision: 1));
        }

        private ValueTask<HostResult<SessionSnapshot>> RejectFilePanelSession()
        {
            Interlocked.Increment(ref _filePanelEnsureCount);
            return ValueTask.FromResult(HostResult<SessionSnapshot>.Fail(
                HostError.Create(
                    HostErrorCode.CapabilityNotSupported,
                    "The test host does not provide file-panel sessions."),
                0));
        }

        private IAsyncEnumerable<WorkspaceGraphStreamItem> WatchWorkspace(
            WatchWorkspaceGraphRequest request,
            CancellationToken cancellationToken)
        {
            Channel<WorkspaceGraphStreamItem> channel;
            lock (_gate)
            {
                channel = WorkspaceEvents(request.WorkspaceId);
            }

            return WatchWorkspaceCore(channel, cancellationToken);
        }

        private async IAsyncEnumerable<WorkspaceGraphStreamItem> WatchWorkspaceCore(
            Channel<WorkspaceGraphStreamItem> channel,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeWatchCount);
            Interlocked.Increment(ref _watchStartCount);
            WatchStarted.TrySetResult();
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return item;
                    if (item is WorkspaceGraphStreamItem.ResynchronizationRequired)
                    {
                        yield break;
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeWatchCount);
                WatchStopped.TrySetResult();
                if (FailWatchWhenCancelled && cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("The cancelled test watch failed during teardown.");
                }
            }
        }

        private Channel<WorkspaceGraphStreamItem> WorkspaceEvents(
            WorkspaceInstanceId workspaceId)
        {
            if (_workspaceEvents.TryGetValue(workspaceId, out var channel))
            {
                return channel;
            }

            channel = Channel.CreateUnbounded<WorkspaceGraphStreamItem>();
            _workspaceEvents.Add(workspaceId, channel);
            return channel;
        }

        private ValueTask<HostResult<Unit>> UnregisterWorkspace(
            UnregisterWorkspaceGraphRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _unregistrations.Add(new WorkspaceUnregistration(request, context));
                var current = _workspace;
                if (current is null
                    || current.WindowId != request.WindowId
                    || current.Workspace.Id != request.WorkspaceId
                    || context.ExpectedRevision != current.Revision)
                {
                    return ValueTask.FromResult(HostResult<Unit>.Fail(
                        HostError.Create(
                            HostErrorCode.RevisionConflict,
                            "The workspace graph is stale."),
                        current?.Revision ?? 0));
                }

                var revision = current.Revision + 1;
                _workspace = null;
                if (AcceptThenCancelNextUnregistration)
                {
                    AcceptThenCancelNextUnregistration = false;
                    throw new OperationCanceledException(
                        "The test host removed the graph but lost the response.",
                        cancellationToken);
                }

                return ValueTask.FromResult(
                    HostResult<Unit>.Succeed(Unit.Value, revision));
            }
        }

        private async ValueTask<HostResult<WorkspaceGraphSnapshot>> ActivateTabAsync(
            ActivateWorkspaceTabRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _activeTabActivations);
            UpdateMaximumConcurrency(concurrent);
            try
            {
                int callNumber;
                bool reject;
                lock (_gate)
                {
                    _tabActivations.Add(new TabActivation(request, context));
                    callNumber = _tabActivations.Count;
                    reject = RejectNextTabActivation;
                    RejectNextTabActivation = false;
                }

                if (DelayFirstTabActivation && callNumber == 1)
                {
                    FirstTabActivationEntered.TrySetResult();
                    await AllowFirstTabActivation.Task.WaitAsync(cancellationToken);
                }

                lock (_gate)
                {
                    var current = _workspace
                        ?? throw new InvalidOperationException("A workspace must be registered first.");
                    if (reject)
                    {
                        return HostResult<WorkspaceGraphSnapshot>.Fail(
                            HostError.Create(
                                HostErrorCode.RevisionConflict,
                                "The test host rejected the activation."),
                            current.Revision);
                    }

                    if (context.ExpectedRevision != current.Revision)
                    {
                        return HostResult<WorkspaceGraphSnapshot>.Fail(
                            HostError.Create(
                                HostErrorCode.RevisionConflict,
                                "The expected revision is stale."),
                            current.Revision);
                    }

                    var activated = current.Workspace.ActivateTab(request.TabId);
                    if (!ReferenceEquals(activated, current.Workspace))
                    {
                        var revision = current.Revision + 1;
                        _workspace = new WorkspaceGraphSnapshot(
                            current.WindowId,
                            activated,
                            revision,
                            current.LastSequence + 1);
                    }

                    var receiptFactory = NextTabActivationReceiptFactory;
                    NextTabActivationReceiptFactory = null;
                    return receiptFactory?.Invoke(_workspace)
                        ?? HostResult<WorkspaceGraphSnapshot>.Succeed(
                            _workspace,
                            _workspace.Revision);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeTabActivations);
            }
        }

        private ValueTask<HostResult<WorkspaceGraphSnapshot>> ActivatePanel(
            ActivateWorkspacePanelRequest request,
            OperationContext context)
        {
            lock (_gate)
            {
                var current = _workspace
                    ?? throw new InvalidOperationException(
                        "A workspace must be registered first.");
                if (context.ExpectedRevision != current.Revision)
                {
                    return ValueTask.FromResult(
                        HostResult<WorkspaceGraphSnapshot>.Fail(
                            HostError.Create(
                                HostErrorCode.RevisionConflict,
                                "The expected revision is stale."),
                            current.Revision));
                }

                var activated = current.Workspace.ActivatePanel(
                    request.TabId,
                    request.PanelId);
                if (!ReferenceEquals(activated, current.Workspace))
                {
                    var revision = current.Revision + 1;
                    _workspace = new WorkspaceGraphSnapshot(
                        current.WindowId,
                        activated,
                        revision,
                        current.LastSequence + 1);
                }

                var receiptFactory = NextPanelActivationReceiptFactory;
                NextPanelActivationReceiptFactory = null;
                return ValueTask.FromResult(
                    receiptFactory?.Invoke(_workspace)
                    ?? HostResult<WorkspaceGraphSnapshot>.Succeed(
                        _workspace,
                        _workspace.Revision));
            }
        }

        private void UpdateMaximumConcurrency(int concurrent)
        {
            var observed = Volatile.Read(ref _maximumConcurrentTabActivations);
            while (concurrent > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref _maximumConcurrentTabActivations,
                    concurrent,
                    observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }

        private static WorkspaceInstance ReplaceFirstPanelSession(
            WorkspaceInstance workspace,
            SessionId sessionId)
        {
            var firstTab = workspace.Tabs[0];
            return workspace.ReplacePanelSession(
                firstTab.Id,
                firstTab.Panels[0].Id,
                sessionId);
        }
    }

    public class FixedCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot Snapshot { get; set; } = DefinitionCatalogSnapshot.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
    }

    /// <summary>
    /// Records the workspace-autosave writes. The batched save arrives either
    /// through <c>SaveWorkspaceWithLayoutsAsync</c> directly or — because
    /// <see cref="DispatchProxy"/> does not intercept default interface
    /// methods — through the individual saves its default implementation
    /// composes, so both shapes record into the same properties.
    /// </summary>
    public class RecordingAutoSaveCatalogProxy : DispatchProxy
    {
        private readonly List<(LayoutDefinition Definition, long? ExpectedRevision)> _layouts = [];

        public DefinitionCatalogSnapshot Snapshot { get; set; } = DefinitionCatalogSnapshot.Empty;

        public WorkspaceDefinition? SavedWorkspace { get; private set; }

        public long? SavedWorkspaceRevision { get; private set; }

        public IReadOnlyList<(LayoutDefinition Definition, long? ExpectedRevision)>
            SavedLayouts => _layouts;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                "SaveWorkspaceWithLayoutsAsync" => RecordBatch(args!),
                "SaveLayoutAsync" => RecordLayout(args!),
                "SaveWorkspaceAsync" => RecordWorkspace(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object RecordBatch(object?[] args)
        {
            _layouts.AddRange(
                (IReadOnlyList<(LayoutDefinition Definition, long? ExpectedRevision)>)args[2]!);
            SavedWorkspace = (WorkspaceDefinition)args[0]!;
            SavedWorkspaceRevision = (long?)args[1];
            return ValueTask.FromResult<DefinitionStoreError?>(null);
        }

        private object RecordLayout(object?[] args)
        {
            var definition = (LayoutDefinition)args[0]!;
            _layouts.Add((definition, (long?)args[1]));
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Success(
                    new StoredDefinition<LayoutDefinition>(
                        definition,
                        1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));
        }

        private object RecordWorkspace(object?[] args)
        {
            SavedWorkspace = (WorkspaceDefinition)args[0]!;
            SavedWorkspaceRevision = (long?)args[1];
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Success(
                    new StoredDefinition<WorkspaceDefinition>(
                        SavedWorkspace,
                        ((long?)args[1] ?? 0) + 1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));
        }
    }

    private sealed class RecordingGovernedAgentRuntime : IGovernedAgentRuntime
    {
        public event EventHandler? Changed;

        public GovernedAgentSnapshot Snapshot { get; private set; } = new(
            GovernedAgentState.Ready,
            RunId: null,
            ProviderId: null,
            Target: null,
            TargetTitle: "No panel selected",
            ContextItems: [],
            Messages: [],
            ProvisionalAssistantText: string.Empty,
            Status: "Choose an active terminal, browser, File Viewer, or Process Monitor panel.");

        public GovernedAgentPrompt? LastRequest { get; private set; }

        public GovernedAgentSteering? LastSteering { get; private set; }

        public int SendCount { get; private set; }

        public int SteeringCount { get; private set; }

        public void SetSnapshot(GovernedAgentSnapshot snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask<GovernedAgentSendResult> SendAsync(
            GovernedAgentPrompt request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            SendCount++;
            return ValueTask.FromResult(
                new GovernedAgentSendResult(
                    true,
                    "agent_turn_completed",
                    "Completed."));
        }

        public ValueTask<GovernedAgentSteeringResult> SteerAsync(
            GovernedAgentSteering request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSteering = request;
            SteeringCount++;
            return ValueTask.FromResult(
                new GovernedAgentSteeringResult(
                    true,
                    "agent_steering_accepted",
                    "Steering accepted."));
        }

        public ValueTask<GovernedAgentDecisionResult> DecideAsync(
            AgentApprovalId approvalId,
            bool approved,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GovernedAgentQuestionResponseResult>
            RespondToQuestionAsync(
                AgentQuestionId questionId,
                GovernedAgentQuestionResponse response,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GovernedAgentCapabilityDecisionResult>
            DecideCapabilityRequestAsync(
                AgentCapabilityRequestId requestId,
                GovernedAgentCapabilityDecision decision,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GovernedAgentStopResult> StopAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new GovernedAgentStopResult(
                    false,
                    "agent_not_running",
                    "No run is active."));

        public ValueTask<GovernedAgentActionCancellationResult>
            CancelActiveActionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new GovernedAgentActionCancellationResult(
                    false,
                    "agent_action_not_running",
                    "No action is active."));

        public ValueTask<GovernedAgentPolicyResult> EnableYoloAsync(
            TimeSpan lifetime,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new GovernedAgentPolicyResult(
                    false,
                    "agent_run_not_bound",
                    "No run is active."));

        public ValueTask<GovernedAgentPolicyResult> DisableYoloAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new GovernedAgentPolicyResult(
                    false,
                    "yolo_not_enabled",
                    "YOLO is not enabled."));

        public ValueTask<bool> ClearAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedAiProfileRuntime(
        IReadOnlyList<AiProviderProfileDescriptor> profiles)
        : IAiProviderProfileRuntime
    {
        public event EventHandler? ProfilesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; } =
            profiles;

        public IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics => [];

        public ValueTask<AiProviderTestResult> TestAsync(
            AiProviderProfile profile,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FixedApprovalPrincipal(ActorDescriptor actor)
        : IAgentApprovalPrincipal
    {
        public ActorDescriptor Actor { get; } = actor;
    }

    private sealed class SelectiveConnectionRuntime(ConnectionId unavailableConnectionId)
        : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.Id == unavailableConnectionId)
            {
                return ValueTask.FromResult(
                    ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                        ConnectionRuntimeError.Create(
                            ConnectionRuntimeErrorCode.RuntimeMissing)));
            }

            return ValueTask.FromResult(
                ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                    new ConnectionOpenPlan(
                        profile.Id,
                        ConnectionKind.Local,
                        new TerminalLaunchRequest(profile.Startup.Directory, "/bin/sh"),
                        ConnectionAuthenticationMode.None,
                        SshHostKeyPolicy.NotApplicable,
                        ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SuccessfulConnectionRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(profile.Startup.Directory, "/bin/sh"),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyFileClients : IFilePanelClient, IFileTransferQueueClient
    {
        public EmptyFileClients(IReadOnlyList<FileProviderProfileDescriptor> profiles)
        {
            Profiles = profiles;
        }

        public EmptyFileClients(bool exposeLocalProfile = false)
        {
            if (!exposeLocalProfile)
            {
                Profiles = [];
                return;
            }

            var root = new FilePanelLocation(
                "test.files.local",
                "local",
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            Profiles =
            [
                new FileProviderProfileDescriptor(
                    "test.files.local",
                    "Test files",
                    FileProviderFamily.Posix,
                    root,
                    FilePanelCapability.List,
                    500,
                    1024 * 1024),
            ];
        }

        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

        public IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; } = [];

        public int ListCallCount { get; private set; }

        public event EventHandler? TransfersChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            ListCallCount++;
            return ValueTask.FromResult(
                FilePanelResult<FilePanelPage>.Success(new FilePanelPage([], null)));
        }

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptySecretVault : ISecretVault
    {
        public SecretVaultAvailability Availability { get; } = new(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.MemoryOnly,
            SecretVaultCapabilities.ListMetadata,
            "test",
            "test_vault",
            "Test vault");

        public void Dispose()
        {
        }

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]));

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SuccessfulAuditStore : IAuditStore
    {
        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success([]));
    }

    private sealed class BlockingUiThreadDispatcher : IUiThreadDispatcher
    {
        public TaskCompletionSource InvocationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            InvocationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            action();
        }
    }

    private sealed class CancellingUiThreadDispatcher : IUiThreadDispatcher
    {
        private static readonly CancellationToken DispatcherStopped =
            new(canceled: true);

        public TaskCompletionSource InvocationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            _ = action;
            _ = cancellationToken;
            InvocationStarted.TrySetResult();
            return Task.FromCanceled(DispatcherStopped);
        }
    }

    private sealed class RecordingUiThreadDispatcher(bool hasAccess) :
        IUiThreadDispatcher
    {
        public int VerifyCount { get; private set; }

        public void VerifyAccess()
        {
            VerifyCount++;
            if (!hasAccess)
            {
                throw new InvalidOperationException(
                    "Presentation teardown requires the UI thread.");
            }
        }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
