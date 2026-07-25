using System.Reflection;
using System.Xml.Linq;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Testing;

namespace GhostShell.App.Tests;

public sealed class McpServerProfileEditorViewModelTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void New_profile_keeps_direct_command_arguments_and_vault_references_separate()
    {
        var editor = NewEditor();
        editor.AddArgument();
        editor.Arguments[0].Value = "--stdio";
        editor.AddArgument();
        editor.Arguments[1].Value = "value with spaces";
        editor.AddEnvironmentBinding();
        editor.Environment[0].Name = "API_TOKEN";
        editor.Environment[0].SecretReference = "vault-token-ref";
        editor.AddEnabledTool();
        editor.EnabledTools[0].Name = "deploy.preview";

        var request = editor.CreateSaveRequest();

        Assert.Equal("/opt/tools/mcp-server", request.Profile.Executable);
        Assert.Equal(["--stdio", "value with spaces"], request.Profile.Arguments);
        var binding = Assert.Single(request.Profile.Environment);
        Assert.Equal("API_TOKEN", binding.Name);
        Assert.Equal(new SecretRef("vault-token-ref"), binding.Reference);
        Assert.Equal(["deploy.preview"], request.Profile.EnabledTools);
        Assert.True(request.RequiresTrustConfirmation);
        Assert.False(request.IsTrustConfirmed);
        Assert.False(request.IsAuthorizedForSave);
        Assert.Equal("/opt/tools/mcp-server", request.TrustReview.Executable);
        Assert.Contains(
            request.TrustReview.Environment,
            item => item.VariableName == "API_TOKEN"
                && item.ReferenceValue == "vault-token-ref"
                && item.State == McpServerCredentialReviewState.Missing);
        Assert.DoesNotContain(
            request.TrustReview.Environment,
            item => item.MetadataSummary.Contains(
                "secret value",
                StringComparison.OrdinalIgnoreCase));

        var confirmed = request.ConfirmTrust();

        Assert.True(confirmed.IsTrustConfirmed);
        Assert.True(confirmed.IsAuthorizedForSave);
    }

    [Fact]
    public void Argument_reordering_changes_the_exact_argv_order_and_accessible_positions()
    {
        var editor = NewEditor();
        editor.AddArgument();
        editor.Arguments[0].Value = "first";
        editor.AddArgument();
        editor.Arguments[1].Value = "second";

        editor.MoveArgumentUp(editor.Arguments[1]);

        var request = editor.CreateSaveRequest();
        Assert.Equal(["second", "first"], request.Profile.Arguments);
        Assert.Equal("Argument 1", editor.Arguments[0].AccessibleName);
        Assert.Equal("Argument 2", editor.Arguments[1].AccessibleName);
    }

    [Fact]
    public void Repeated_environment_and_tool_rows_keep_unique_accessible_positions()
    {
        var editor = NewEditor();
        editor.AddEnvironmentBinding();
        editor.AddEnvironmentBinding();
        editor.AddEnvironmentBinding();
        editor.AddEnabledTool();
        editor.AddEnabledTool();
        editor.AddEnabledTool();

        editor.RemoveEnvironmentBinding(editor.Environment[0]);
        editor.RemoveEnabledTool(editor.EnabledTools[1]);

        Assert.Equal(
            [
                "Environment binding 1 variable name",
                "Environment binding 2 variable name",
            ],
            editor.Environment.Select(item => item.NameAccessibleName));
        Assert.Equal(
            [
                "Environment binding 1 secret reference",
                "Environment binding 2 secret reference",
            ],
            editor.Environment.Select(item => item.SecretReferenceAccessibleName));
        Assert.Equal(
            [
                "Remove environment binding 1",
                "Remove environment binding 2",
            ],
            editor.Environment.Select(item => item.RemoveAccessibleName));
        Assert.Equal(
            ["Enabled MCP tool 1 name", "Enabled MCP tool 2 name"],
            editor.EnabledTools.Select(item => item.NameAccessibleName));
        Assert.Equal(
            ["Remove enabled MCP tool 1", "Remove enabled MCP tool 2"],
            editor.EnabledTools.Select(item => item.RemoveAccessibleName));
    }

    [Fact]
    public void Trust_review_shows_credential_metadata_and_scope_state()
    {
        var profile = Profile(enabledTools: ["read"]);
        var matching = Secret(
            profile.Environment[0].Reference,
            "Deployment token",
            "ApiKey",
            new SecretScope(
                SecretScopeKind.McpServer,
                profile.Id.Value));
        var wrongScope = Secret(
            profile.Environment[0].Reference,
            "Global token",
            "Password",
            SecretScope.Global);

        var availableReview = new McpServerProfileEditorViewModel(
            profile,
            expectedRevision: 1,
            secrets: [matching])
            .CreateSaveRequest()
            .TrustReview;
        var wrongScopeReview = new McpServerProfileEditorViewModel(
            profile,
            expectedRevision: 1,
            secrets: [wrongScope])
            .CreateSaveRequest()
            .TrustReview;
        var missingReview = new McpServerProfileEditorViewModel(
            profile,
            expectedRevision: 1,
            secrets: [])
            .CreateSaveRequest()
            .TrustReview;

        var available = Assert.Single(availableReview.Environment);
        Assert.Equal("Deployment token", available.CredentialLabel);
        Assert.Equal("ApiKey", available.CredentialKind);
        Assert.Equal(
            McpServerCredentialReviewState.Available,
            available.State);
        Assert.Equal(
            McpServerCredentialReviewState.WrongScope,
            Assert.Single(wrongScopeReview.Environment).State);
        Assert.Contains(
            "Global token",
            Assert.Single(wrongScopeReview.Environment).MetadataSummary,
            StringComparison.Ordinal);
        Assert.Equal(
            McpServerCredentialReviewState.Missing,
            Assert.Single(missingReview.Environment).State);
    }

    [Fact]
    public void Metadata_changes_and_allowlist_narrowing_do_not_require_expansion_confirmation()
    {
        var existing = Profile(enabledTools: ["read", "write"]);
        var editor = new McpServerProfileEditorViewModel(existing, expectedRevision: 7)
        {
            Name = "Renamed server",
            IsEnabled = false,
        };
        editor.RemoveEnabledTool(editor.EnabledTools.Single(item => item.Name == "write"));

        var request = editor.CreateSaveRequest();

        Assert.Equal(7, request.ExpectedRevision);
        Assert.False(request.RequiresTrustConfirmation);
        Assert.True(request.IsAuthorizedForSave);
        Assert.Empty(request.TrustReview.Changes);
        Assert.Equal(["read"], request.Profile.EnabledTools);
    }

    [Fact]
    public void Launch_changes_environment_changes_and_tool_expansion_require_confirmation()
    {
        var mutations = new Action<McpServerProfileEditorViewModel>[]
        {
            editor => editor.Executable = "/opt/tools/replacement-server",
            editor =>
            {
                editor.AddArgument();
                editor.Arguments[^1].Value = "--verbose";
            },
            editor => editor.WorkingDirectory = "/var/tmp/mcp",
            editor => editor.Environment[0].SecretReference = "replacement-ref",
            editor =>
            {
                editor.AddEnabledTool();
                editor.EnabledTools[^1].Name = "write";
            },
        };

        foreach (var mutate in mutations)
        {
            var editor = new McpServerProfileEditorViewModel(
                Profile(enabledTools: ["read"]),
                expectedRevision: 3);
            mutate(editor);

            var request = editor.CreateSaveRequest();

            Assert.True(request.RequiresTrustConfirmation);
            Assert.False(request.IsAuthorizedForSave);
            Assert.NotEmpty(request.TrustReview.Changes);
        }
    }

    [Fact]
    public void Enabling_a_previously_disabled_server_requires_confirmation()
    {
        var editor = new McpServerProfileEditorViewModel(
            Profile(enabledTools: ["read"], isEnabled: false),
            expectedRevision: 4)
        {
            IsEnabled = true,
        };

        var request = editor.CreateSaveRequest();

        Assert.True(request.RequiresTrustConfirmation);
        Assert.Contains(
            request.TrustReview.Changes,
            change => change.Contains("Enable this server", StringComparison.Ordinal));
    }

    [Fact]
    public void Relative_launch_paths_are_rejected_before_review()
    {
        var editor = NewEditor();
        editor.Executable = "mcp-server";

        var executableError = Assert.Throws<ArgumentException>(
            editor.CreateSaveRequest);

        Assert.Contains(
            "fully qualified",
            executableError.Message,
            StringComparison.OrdinalIgnoreCase);

        editor.Executable = "/opt/tools/mcp-server";
        editor.WorkingDirectory = "relative/work";

        var directoryError = Assert.Throws<ArgumentException>(
            editor.CreateSaveRequest);

        Assert.Contains(
            "working directory",
            directoryError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Server_list_projection_reports_when_an_enabled_profile_has_no_tools()
    {
        var profile = Profile(enabledTools: []);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            9,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var item = MainWindowViewModel.ProjectMcpServerProfile(stored, []);

        Assert.Equal("No tools enabled", item.Status);
        Assert.Contains("excluded", item.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.True(item.HasWarning);
        Assert.Equal("1 ordered arg", item.ArgumentSummary);
        Assert.Equal("1 vault binding", item.EnvironmentBindingSummary);
        Assert.Equal("0 enabled tools", item.EnabledToolSummary);
    }

    [Fact]
    public void Server_list_projection_requires_a_matching_profile_scoped_credential()
    {
        var profile = Profile(enabledTools: ["read"]);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            4,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var wrongScope = new SecretMetadataViewModel(
            new SecretRef("vault-ref"),
            "Token",
            "ApiKey",
            "Global",
            "Never",
            "Never",
            SecretScope.Global,
            "None",
            0);

        var missing = MainWindowViewModel.ProjectMcpServerProfile(
            stored,
            [wrongScope]);
        var matching = MainWindowViewModel.ProjectMcpServerProfile(
            stored,
            [wrongScope with
            {
                SecretScope = new SecretScope(
                    SecretScopeKind.McpServer,
                    profile.Id.Value),
            }]);

        Assert.Equal("Credential missing", missing.Status);
        Assert.True(missing.HasWarning);
        Assert.Equal("Enabled for new runs", matching.Status);
        Assert.Contains(
            "does not show live MCP process state",
            matching.StatusDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(matching.HasWarning);
        Assert.Equal("1 enabled tool", matching.EnabledToolSummary);
    }

    [Fact]
    public void Editor_and_trust_review_are_keyboard_and_screen_reader_accessible()
    {
        XNamespace view = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var editor = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "McpServerProfileEditorDialog.axaml"));
        var confirmation = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "McpServerTrustConfirmationDialog.axaml"));

        Assert.Equal("OnOpened", editor.Root?.Attribute("Opened")?.Value);
        Assert.NotNull(editor.Descendants(view + "TextBox").Single(element =>
            element.Attribute(x + "Name")?.Value == "McpServerNameInput"));
        Assert.NotNull(editor.Descendants(view + "TextBlock").Single(element =>
            element.Attribute(x + "Name")?.Value == "ValidationError"
            && element.Attribute("Focusable")?.Value == "True"
            && !string.IsNullOrWhiteSpace(
                element.Attribute("AutomationProperties.Name")?.Value)));
        Assert.Contains(
            editor.Descendants(view + "TextBlock"),
            element => element.Attribute("Text")?.Value?.Contains(
                "not a shell command",
                StringComparison.OrdinalIgnoreCase) == true);
        foreach (var iconButton in editor
                     .Descendants(view + "Button")
                     .Where(button => button.Descendants(view + "SymbolIcon").Any()))
        {
            Assert.False(string.IsNullOrWhiteSpace(
                iconButton.Attribute("ToolTip.Tip")?.Value));
            Assert.False(string.IsNullOrWhiteSpace(
                iconButton.Attribute("AutomationProperties.Name")?.Value));
        }

        var editorCancel = Assert.Single(
            editor.Descendants(view + "Button"),
            button => button.Attribute("Content")?.Value == "Cancel");
        Assert.Equal("True", editorCancel.Attribute("IsCancel")?.Value);
        var editorSave = Assert.Single(
            editor.Descendants(view + "Button"),
            button => button.Attribute("Content")?.Value == "Review and save");
        Assert.Equal("True", editorSave.Attribute("IsDefault")?.Value);

        Assert.Equal("OnOpened", confirmation.Root?.Attribute("Opened")?.Value);
        var acknowledgement = Assert.Single(
            confirmation.Descendants(view + "CheckBox"),
            element => element.Attribute(x + "Name")?.Value == "Acknowledgement");
        Assert.False(string.IsNullOrWhiteSpace(
            acknowledgement.Attribute("AutomationProperties.Name")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(
            acknowledgement.Attribute("AutomationProperties.HelpText")?.Value));
        var confirm = Assert.Single(
            confirmation.Descendants(view + "Button"),
            button => button.Attribute(x + "Name")?.Value == "ConfirmButton");
        Assert.Equal("False", confirm.Attribute("IsEnabled")?.Value);
        Assert.Equal("True", confirm.Attribute("IsDefault")?.Value);
        Assert.Contains(
            confirmation.Descendants(view + "TextBlock"),
            element => element.Attribute("Text")?.Value?.Contains(
                "Credential values are never loaded or displayed",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Settings_surface_has_navigation_empty_state_health_and_credential_controls()
    {
        XNamespace view = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var ownedSection = ApplicationViews.FindUniqueNamedElement(
            "McpSettingsSection");
        var mainWindow = ownedSection.Owner.Document;
        var section = ownedSection.Element;

        Assert.Equal(view + "StackPanel", section.Name);
        Assert.Equal(
            "{Binding IsMcpSettingsVisible}",
            section.Attribute("IsVisible")?.Value);
        Assert.Contains(
            mainWindow.Descendants(view + "Button"),
            button => button.Attribute("Click")?.Value == "OnMcpSettingsClick"
                && !string.IsNullOrWhiteSpace(
                    button.Attribute("AutomationProperties.Name")?.Value));
        Assert.Contains(
            section.Descendants(view + "TextBlock"),
            element => element.Attribute("Text")?.Value == "No MCP server configured");
        Assert.Contains(
            section.Descendants(view + "TextBlock"),
            element => element.Attribute("Text")?.Value?.Contains(
                "not live MCP process state",
                StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            section.Descendants(view + "Button"),
            button =>
                button.Attribute("Click")?.Value
                    == "OnTestMcpServerClick"
                && button.Attribute("IsEnabled")?.Value
                    == "{Binding CanTest}"
                && !string.IsNullOrWhiteSpace(
                    button.Attribute(
                        "AutomationProperties.Name")?.Value));
        Assert.DoesNotContain(
            section.Descendants(view + "TextBlock"),
            element => element.Attribute("Text")?.Value?.Contains(
                "No process is started",
                StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(section.Descendants(view + "ComboBox").Single(element =>
            element.Attribute(x + "Name")?.Value == "McpEnvironmentSecretTargetPicker"));
        Assert.NotNull(section.Descendants(view + "TextBox").Single(element =>
            element.Attribute(x + "Name")?.Value == "McpServerSecretValueInput"
            && element.Attribute("PasswordChar")?.Value == "●"));
        Assert.All(
            section.Descendants(view + "Button").Where(button =>
                button.Descendants(view + "SymbolIcon").Any()),
            button =>
            {
                Assert.False(string.IsNullOrWhiteSpace(
                    button.Attribute("ToolTip.Tip")?.Value));
                Assert.False(string.IsNullOrWhiteSpace(
                    button.Attribute("AutomationProperties.Name")?.Value));
            });
    }

    [Fact]
    public async Task Settings_test_preserves_row_identity_and_reports_bounded_counts()
    {
        var profile = Profile(
            enabledTools: ["read"],
            hasEnvironment: false);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            11,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles = [stored],
            };
        var diagnostics = DispatchProxy.Create<
            IMcpServerDiagnostics,
            McpDiagnosticsProxy>();
        var diagnosticsProxy =
            (McpDiagnosticsProxy)(object)diagnostics;
        diagnosticsProxy.Result = new McpServerTestResult.Success(
            new McpServerTestReport(
                profile.Id,
                stored.Revision,
                discoveredToolCount: 2,
                enabledToolCount: 1,
                DateTimeOffset.UnixEpoch));
        var filePanel = DispatchProxy.Create<
            IFilePanelClient,
            FilePanelProxy>();
        var transfers = DispatchProxy.Create<
            IFileTransferQueueClient,
            FileTransferQueueProxy>();
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<
                ISessionHostClient,
                RejectingProxy>(),
            catalog,
            DispatchProxy.Create<
                IConnectionRuntime,
                RejectingProxy>(),
            DispatchProxy.Create<
                ISecretVault,
                SecretVaultProxy>(),
            filePanel,
            transfers,
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System),
            mcpServerDiagnostics: diagnostics);
        var item = Assert.Single(
            viewModel.McpServerDefinitions);

        await viewModel.TestMcpServerAsync(
            item,
            CancellationToken.None);

        var tested = Assert.Single(
            viewModel.McpServerDefinitions);
        Assert.Same(item, tested);
        Assert.Equal("Last test passed", tested.Status);
        Assert.Contains(
            "directly launched process",
            tested.StatusDetail,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not show live process state",
            tested.StatusDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(tested.CanTest);
        Assert.False(tested.IsTesting);
        Assert.Equal(1, diagnosticsProxy.CallCount);
        Assert.Equal(
            stored.Revision,
            diagnosticsProxy.Request!.ExpectedRevision);
        Assert.Equal(
            ActorKind.Human,
            diagnosticsProxy.Context!.Actor.Kind);
        Assert.Equal(
            stored.Revision,
            diagnosticsProxy.Context.ExpectedRevision);
    }

    [Fact]
    public async Task Settings_test_keeps_the_same_row_while_diagnostics_are_pending()
    {
        var profile = Profile(
            enabledTools: ["read"],
            hasEnvironment: false);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            12,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles = [stored],
            };
        var diagnostics = DispatchProxy.Create<
            IMcpServerDiagnostics,
            McpDiagnosticsProxy>();
        var diagnosticsProxy =
            (McpDiagnosticsProxy)(object)diagnostics;
        diagnosticsProxy.PendingResult = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var filePanel = DispatchProxy.Create<
            IFilePanelClient,
            FilePanelProxy>();
        var transfers = DispatchProxy.Create<
            IFileTransferQueueClient,
            FileTransferQueueProxy>();
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<
                ISessionHostClient,
                RejectingProxy>(),
            catalog,
            DispatchProxy.Create<
                IConnectionRuntime,
                RejectingProxy>(),
            DispatchProxy.Create<
                ISecretVault,
                SecretVaultProxy>(),
            filePanel,
            transfers,
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System),
            mcpServerDiagnostics: diagnostics);
        var original = Assert.Single(viewModel.McpServerDefinitions);

        var testTask = viewModel.TestMcpServerAsync(
                original,
                CancellationToken.None)
            .AsTask();

        var pending = Assert.Single(viewModel.McpServerDefinitions);
        Assert.Same(original, pending);
        Assert.True(pending.IsTesting);
        Assert.Equal("Testing…", pending.TestActionLabel);

        diagnosticsProxy.PendingResult.SetResult(
            new McpServerTestResult.Success(
                new McpServerTestReport(
                    profile.Id,
                    stored.Revision,
                    discoveredToolCount: 1,
                    enabledToolCount: 1,
                    DateTimeOffset.UnixEpoch)));
        await testTask;

        Assert.Same(
            original,
            Assert.Single(viewModel.McpServerDefinitions));
        Assert.False(original.IsTesting);
        Assert.Equal("Last test passed", original.Status);
    }

    [Fact]
    public async Task ReplacingAnMcpCredentialInvalidatesOnlyMcpSessions()
    {
        var mcpReference = new SecretRef("vault-mcp-token");
        var mcpScope = new SecretScope(
            SecretScopeKind.McpServer,
            "mcp.local-tools");
        var profile = new McpServerProfile(
            new McpServerProfileId("mcp.local-tools"),
            McpServerProfile.CurrentSchemaVersion,
            "Local tools",
            "/opt/tools/mcp-server",
            ["--stdio"],
            "/var/lib/mcp",
            [new McpServerEnvironmentVariable(
                "API_TOKEN",
                mcpReference)],
            ["read"]);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            13,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles = [stored],
            };
        var vault = DispatchProxy.Create<
            ISecretVault,
            SecretVaultProxy>();
        ((SecretVaultProxy)(object)vault).Metadata =
        [
            new SecretMetadata(
                mcpReference,
                "MCP token",
                SecretKind.ApiKey,
                mcpScope,
                SecretVaultPersistenceKind.MemoryOnly,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
        ];
        var invalidator = DispatchProxy.Create<
            IMcpCredentialSessionInvalidator,
            McpCredentialInvalidatorProxy>();
        var diagnostics = DispatchProxy.Create<
            IMcpServerDiagnostics,
            McpDiagnosticsProxy>();
        ((McpDiagnosticsProxy)(object)diagnostics).Result =
            new McpServerTestResult.Success(
                new McpServerTestReport(
                    profile.Id,
                    stored.Revision,
                    discoveredToolCount: 1,
                    enabledToolCount: 1,
                    DateTimeOffset.UnixEpoch));
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<
                ISessionHostClient,
                RejectingProxy>(),
            catalog,
            DispatchProxy.Create<
                IConnectionRuntime,
                RejectingProxy>(),
            vault,
            DispatchProxy.Create<
                IFilePanelClient,
                FilePanelProxy>(),
            DispatchProxy.Create<
                IFileTransferQueueClient,
                FileTransferQueueProxy>(),
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System),
            mcpServerDiagnostics: diagnostics,
            mcpCredentialSessionInvalidator: invalidator);
        var mcpSecret = Secret(
            mcpReference,
            "MCP token",
            "ApiKey",
            mcpScope);
        var globalSecret = Secret(
            new SecretRef("vault-global-token"),
            "Global token",
            "ApiKey",
            SecretScope.Global);
        using var firstReplacement =
            SecretMaterial.CopyFrom("first"u8);
        using var secondReplacement =
            SecretMaterial.CopyFrom("second"u8);

        var row = Assert.Single(viewModel.McpServerDefinitions);
        await viewModel.TestMcpServerAsync(
            row,
            CancellationToken.None);
        Assert.Equal("Last test passed", row.Status);

        Assert.True(await viewModel.ReplaceSecretAsync(
            mcpSecret,
            firstReplacement,
            CancellationToken.None));
        Assert.True(await viewModel.ReplaceSecretAsync(
            globalSecret,
            secondReplacement,
            CancellationToken.None));

        var invalidatorProxy =
            (McpCredentialInvalidatorProxy)(object)invalidator;
        Assert.Equal([mcpReference], invalidatorProxy.References);
        Assert.Same(
            row,
            Assert.Single(viewModel.McpServerDefinitions));
        Assert.Equal(
            "Enabled for new runs",
            row.Status);
        Assert.DoesNotContain(
            "test passed",
            row.StatusDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            2,
            ((SecretVaultProxy)(object)vault).ReplaceCount);
    }

    private static McpServerProfileEditorViewModel NewEditor() =>
        new()
        {
            Name = "Local tools",
            Executable = "/opt/tools/mcp-server",
            WorkingDirectory = "/var/lib/mcp",
        };

    private static McpServerProfile Profile(
        IReadOnlyList<string> enabledTools,
        bool isEnabled = true,
        bool hasEnvironment = true) =>
        new(
            new McpServerProfileId("mcp.local-tools"),
            McpServerProfile.CurrentSchemaVersion,
            "Local tools",
            "/opt/tools/mcp-server",
            ["--stdio"],
            "/var/lib/mcp",
            hasEnvironment
                ? [new McpServerEnvironmentVariable(
                    "API_TOKEN",
                    new SecretRef("vault-ref"))]
                : [],
            enabledTools,
            isEnabled);

    private static SecretMetadataViewModel Secret(
        SecretRef reference,
        string label,
        string kind,
        SecretScope scope) =>
        new(
            reference,
            label,
            kind,
            scope.Kind.ToString(),
            "Never",
            "Never",
            scope,
            "MCP server",
            1);

    public class CatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot SnapshotValue { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => SnapshotValue,
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
    }

    public class McpDiagnosticsProxy : DispatchProxy
    {
        public McpServerTestResult Result { get; set; } =
            new McpServerTestResult.Failure(
                new McpServerTestError(
                    "mcp_test_failed",
                    "Test failure.",
                    retryable: false));

        public TaskCompletionSource<McpServerTestResult>? PendingResult
        {
            get;
            set;
        }

        public int CallCount { get; private set; }

        public McpServerTestRequest? Request { get; private set; }

        public OperationContext? Context { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments)
        {
            if (targetMethod?.Name
                    == nameof(IMcpServerDiagnostics.TestAsync)
                && arguments is
                [
                    McpServerTestRequest request,
                    OperationContext context,
                    CancellationToken cancellationToken,
                ])
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                Request = request;
                Context = context;
                return PendingResult is null
                    ? ValueTask.FromResult(Result)
                    : new ValueTask<McpServerTestResult>(
                        PendingResult.Task);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class SecretVaultProxy : DispatchProxy
    {
        public IReadOnlyList<SecretMetadata> Metadata { get; set; } = [];

        public int ReplaceCount { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments)
        {
            if (targetMethod?.Name == nameof(ISecretVault.ReplaceAsync)
                && arguments is
                [
                    ReplaceSecretRequest request,
                    SecretMaterial,
                    CancellationToken,
                ])
            {
                ReplaceCount++;
                return ValueTask.FromResult(
                    SecretVaultResult<SecretMetadata>.Succeed(
                        new SecretMetadata(
                            request.Reference,
                            "Updated credential",
                            SecretKind.ApiKey,
                            request.Scope,
                            SecretVaultPersistenceKind.MemoryOnly,
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch)));
            }

            return targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.MemoryOnly,
                    SecretVaultCapabilities.ListMetadata,
                    "test",
                    "test_available",
                    "Test vault is available."),
                nameof(ISecretVault.ListMetadataAsync) =>
                    ValueTask.FromResult(
                        SecretVaultResult<
                            IReadOnlyList<SecretMetadata>>.Succeed(
                                Metadata)),
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
        }
    }

    public class McpCredentialInvalidatorProxy : DispatchProxy
    {
        public List<SecretRef> References { get; } = [];

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments)
        {
            if (targetMethod?.Name
                    == nameof(
                        IMcpCredentialSessionInvalidator
                            .InvalidateAsync)
                && arguments is [SecretRef reference])
            {
                References.Add(reference);
                return ValueTask.CompletedTask;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class FilePanelProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                "get_Profiles" =>
                    Array.Empty<FileProviderProfileDescriptor>(),
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
    }

    public class FileTransferQueueProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                "get_Transfers" =>
                    Array.Empty<FilePanelTransferSnapshot>(),
                "add_TransfersChanged"
                    or "remove_TransfersChanged" => null,
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
    }

    public class RejectingProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class UnusedAuditStore : IAuditStore
    {
        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<
            AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

}
