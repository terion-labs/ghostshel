using GhostShell.Core;

namespace GhostShell.Application;

public sealed class DefinitionCatalog : IDefinitionCatalog
{
    private readonly IDefinitionRepository<ConnectionProfile> _connections;
    private readonly IDefinitionRepository<LayoutDefinition> _layouts;
    private readonly IDefinitionRepository<ScreenDefinition> _screens;
    private readonly IDefinitionRepository<WorkspaceDefinition> _workspaces;
    private readonly IDefinitionRepository<ThemePreference> _themes;
    private readonly IDefinitionRepository<TerminalProfile> _terminalProfiles;
    private readonly IDefinitionRepository<KeymapProfile> _keymaps;
    private readonly IDefinitionRepository<FileProviderProfile> _fileProviderProfiles;
    private readonly IDefinitionRepository<AiProviderProfile> _aiProviderProfiles;
    private readonly IDefinitionRepository<McpServerProfile> _mcpServerProfiles;
    private readonly IDefinitionRepository<QuickTerminalSettings> _quickTerminalSettings;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private DefinitionCatalogSnapshot _snapshot = DefinitionCatalogSnapshot.Empty;
    private bool _initialized;

    public DefinitionCatalog(
        IDefinitionRepository<ConnectionProfile> connections,
        IDefinitionRepository<LayoutDefinition> layouts,
        IDefinitionRepository<ScreenDefinition> screens,
        IDefinitionRepository<WorkspaceDefinition> workspaces,
        IDefinitionRepository<ThemePreference> themes,
        IDefinitionRepository<TerminalProfile> terminalProfiles,
        IDefinitionRepository<KeymapProfile> keymaps,
        IDefinitionRepository<FileProviderProfile> fileProviderProfiles,
        IDefinitionRepository<AiProviderProfile> aiProviderProfiles,
        IDefinitionRepository<McpServerProfile> mcpServerProfiles,
        IDefinitionRepository<QuickTerminalSettings> quickTerminalSettings)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _themes = themes ?? throw new ArgumentNullException(nameof(themes));
        _terminalProfiles = terminalProfiles
            ?? throw new ArgumentNullException(nameof(terminalProfiles));
        _keymaps = keymaps ?? throw new ArgumentNullException(nameof(keymaps));
        _fileProviderProfiles = fileProviderProfiles
            ?? throw new ArgumentNullException(nameof(fileProviderProfiles));
        _aiProviderProfiles = aiProviderProfiles
            ?? throw new ArgumentNullException(nameof(aiProviderProfiles));
        _mcpServerProfiles = mcpServerProfiles
            ?? throw new ArgumentNullException(nameof(mcpServerProfiles));
        _quickTerminalSettings = quickTerminalSettings
            ?? throw new ArgumentNullException(nameof(quickTerminalSettings));
    }

    public DefinitionCatalogSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler? Changed;

    public async ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(Snapshot);
            }

            var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                return refreshed;
            }

            var seeded = await SeedDefaultsAsync(cancellationToken).ConfigureAwait(false);
            if (!seeded.IsSuccess)
            {
                return DefinitionStoreResult<DefinitionCatalogSnapshot>.Failure(seeded.Error!);
            }

            refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                _initialized = true;
            }

            return refreshed;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Rebuilds the published snapshot from durable storage without running first-use seeding.
    /// A failed reload leaves the previously published snapshot intact.
    /// </summary>
    public async ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return refreshed;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _connections,
            ValidateConnectionName,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
        LayoutDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _layouts,
            ValidateLayout,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
        ScreenDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _screens,
            ValidateScreen,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
        WorkspaceDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _workspaces,
            ValidateWorkspace,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        ThemePreference definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _themes,
            ValidateThemeName,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        TerminalProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _terminalProfiles,
            ValidateTerminalProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
        KeymapProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _keymaps,
            ValidateKeymap,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
        FileProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _fileProviderProfiles,
            ValidateFileProviderProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
        AiProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _aiProviderProfiles,
            ValidateAiProviderProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
        McpServerProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _mcpServerProfiles,
            ValidateMcpServerProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        QuickTerminalSettings definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _quickTerminalSettings,
            ValidateQuickTerminalSettings,
            cancellationToken);

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (key.Kind == AiProviderProfile.Kind
                && IsAiProviderPolicyDependency(key.Value))
            {
                return DefinitionStoreResult<Unit>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.DependencyConflict,
                        "This AI-provider profile is referenced by a saved screen or workspace agent policy."));
            }

            var result = key.Kind switch
            {
                var kind when kind == ConnectionProfile.Kind =>
                    await _connections.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == LayoutDefinition.Kind =>
                    await _layouts.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == ScreenDefinition.Kind =>
                    await _screens.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == WorkspaceDefinition.Kind =>
                    await _workspaces.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == ThemePreference.Kind =>
                    await _themes.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == TerminalProfile.Kind =>
                    await _terminalProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == KeymapProfile.Kind =>
                    await _keymaps.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == FileProviderProfile.Kind =>
                    await _fileProviderProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == AiProviderProfile.Kind =>
                    await _aiProviderProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == McpServerProfile.Kind =>
                    await _mcpServerProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == QuickTerminalSettings.Kind =>
                    await _quickTerminalSettings.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                _ => DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                    DefinitionStoreErrorCode.UnsupportedKind,
                    "This definition kind cannot be deleted by the current application.")),
            };
            if (result.IsSuccess)
            {
                var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (!refreshed.IsSuccess)
                {
                    return DefinitionStoreResult<Unit>.Failure(refreshed.Error!);
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> SaveValidatedAsync<TDefinition>(
        TDefinition definition,
        long? expectedRevision,
        IDefinitionRepository<TDefinition> repository,
        Func<TDefinition, DefinitionStoreError?> validate,
        CancellationToken cancellationToken)
        where TDefinition : IDurableDefinition
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var error = validate(definition);
            if (error is not null)
            {
                return DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(error);
            }

            var result = await repository.SaveAsync(
                    definition,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (!refreshed.IsSuccess)
                {
                    return DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(
                        refreshed.Error!);
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private DefinitionStoreError? ValidateConnectionName(ConnectionProfile definition) =>
        ValidateName(Snapshot.Connections, definition);

    private DefinitionStoreError? ValidateThemeName(ThemePreference definition) =>
        ValidateName(Snapshot.Themes, definition);

    private DefinitionStoreError? ValidateLayout(LayoutDefinition definition)
    {
        var duplicate = ValidateName(Snapshot.Layouts, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var validation = LayoutValidator.Validate(definition);
        if (!validation.IsValid)
        {
            return Invalid(validation);
        }

        foreach (var screen in Snapshot.Screens
                     .Where(item => item.Value.LayoutId == definition.Id))
        {
            var screenValidation = ScreenValidator.Validate(screen.Value, definition);
            if (!screenValidation.IsValid)
            {
                return new DefinitionStoreError(
                    DefinitionStoreErrorCode.DependencyConflict,
                    $"The layout change would invalidate saved screen '{screen.Value.Name}'.");
            }
        }

        return null;
    }

    private DefinitionStoreError? ValidateScreen(ScreenDefinition definition)
    {
        var duplicate = ValidateName(Snapshot.Screens, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var layout = Snapshot.Layouts
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == definition.LayoutId);
        if (layout is null)
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "The selected layout no longer exists.");
        }

        var validation = ScreenValidator.Validate(definition, layout);
        if (!validation.IsValid)
        {
            return Invalid(validation);
        }

        var policyDependency = ValidateAgentPolicyProviderDependency(
            definition.AgentPolicyOverride);
        if (policyDependency is not null)
        {
            return policyDependency;
        }

        var connectionIds = Snapshot.Connections.Select(item => item.Value.Id).ToHashSet();
        if (definition.Panels.Any(panel =>
                panel.ConnectionId is { } id && !connectionIds.Contains(id)))
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "At least one panel references a connection that no longer exists.");
        }

        var fileProviderIds = Snapshot.FileProviderProfiles
            .Select(item => item.Value.Id)
            .ToHashSet();
        return definition.Panels.Any(panel =>
            panel.FileProviderProfileId is { } id
            && !BuiltInFileProviders.IsIntrinsic(id)
            && !fileProviderIds.Contains(id))
            ? new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "At least one panel references a file-provider profile that no longer exists.")
            : null;
    }

    private DefinitionStoreError? ValidateWorkspace(WorkspaceDefinition definition)
    {
        var duplicate = ValidateName(Snapshot.Workspaces, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var validation = WorkspaceValidator.Validate(definition);
        if (!validation.IsValid)
        {
            return Invalid(validation);
        }

        var policyDependency = ValidateAgentPolicyProviderDependency(
            definition.AgentPolicyOverride);
        if (policyDependency is not null)
        {
            return policyDependency;
        }

        var connectionIds = Snapshot.Connections.Select(item => item.Value.Id).ToHashSet();
        var screenIds = Snapshot.Screens.Select(item => item.Value.Id).ToHashSet();
        var fileProviderIds = Snapshot.FileProviderProfiles
            .Select(item => item.Value.Id)
            .ToHashSet();
        var layouts = Snapshot.Layouts.ToDictionary(item => item.Value.Id, item => item.Value);
        foreach (var entry in definition.Entries)
        {
            switch (entry)
            {
                case WorkspaceEntry.ConnectionReference connection
                    when !connectionIds.Contains(connection.ConnectionId):
                    return MissingWorkspaceDependency("connection", connection.ConnectionId.Value);
                case WorkspaceEntry.ScreenReference screen when !screenIds.Contains(screen.ScreenId):
                    return MissingWorkspaceDependency("screen", screen.ScreenId.Value);
                case WorkspaceEntry.Tab tab when !layouts.TryGetValue(tab.LayoutId, out _):
                    return MissingWorkspaceDependency("layout", tab.LayoutId.Value);
                case WorkspaceEntry.Tab tab:
                    var resolvedLayout = layouts[tab.LayoutId];
                    var screenShape = new ScreenDefinition(
                        new ScreenId($"workspace-tab-{tab.Id.Value}"),
                        ScreenDefinition.CurrentSchemaVersion,
                        tab.Name,
                        null,
                        tab.LayoutId,
                        tab.Panels);
                    var tabValidation = ScreenValidator.Validate(screenShape, resolvedLayout);
                    if (!tabValidation.IsValid)
                    {
                        return Invalid(tabValidation);
                    }

                    if (tab.Panels.Any(panel =>
                        panel.ConnectionId is { } id && !connectionIds.Contains(id)))
                    {
                        return MissingWorkspaceDependency("connection", "workspace-only tab");
                    }

                    if (tab.Panels.Any(panel =>
                        panel.FileProviderProfileId is { } id
                        && !BuiltInFileProviders.IsIntrinsic(id)
                        && !fileProviderIds.Contains(id)))
                    {
                        return MissingWorkspaceDependency(
                            "file-provider profile",
                            "workspace-only tab");
                    }

                    break;
            }
        }

        return null;
    }

    private DefinitionStoreError? ValidateAgentPolicyProviderDependency(
        AgentPolicy? policy)
    {
        if (policy is null)
        {
            return null;
        }

        AiProviderProfileId profileId;
        try
        {
            profileId = new AiProviderProfileId(policy.Provider);
        }
        catch (ArgumentException)
        {
            return MissingAgentPolicyProvider();
        }

        return Snapshot.AiProviderProfiles.Any(item =>
            item.Value.Id == profileId && item.Value.IsEnabled)
            ? null
            : MissingAgentPolicyProvider();
    }

    private bool IsAiProviderPolicyDependency(string providerId) =>
        Snapshot.Screens.Any(item =>
            string.Equals(
                item.Value.AgentPolicyOverride?.Provider,
                providerId,
                StringComparison.Ordinal))
        || Snapshot.Workspaces.Any(item =>
            string.Equals(
                item.Value.AgentPolicyOverride?.Provider,
                providerId,
                StringComparison.Ordinal));

    private static DefinitionStoreError MissingAgentPolicyProvider() =>
        new(
            DefinitionStoreErrorCode.DependencyConflict,
            "The agent policy requires an existing enabled AI-provider profile.");

    private DefinitionStoreError? ValidateTerminalProfile(TerminalProfile definition)
    {
        var duplicate = ValidateName(Snapshot.TerminalProfiles, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        return Snapshot.Keymaps.Any(item => item.Value.Id == definition.KeymapId)
            || BuiltInKeymaps.All.Any(item => item.Id == definition.KeymapId)
            ? null
            : new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "The selected terminal keymap does not exist.");
    }

    private DefinitionStoreError? ValidateKeymap(KeymapProfile definition)
    {
        var duplicate = ValidateName(Snapshot.Keymaps, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var issues = KeymapConflictValidator.Validate(definition, BuiltInCommands.Registry);
        var errors = issues.Where(issue => issue.Severity == KeymapIssueSeverity.Error).ToArray();
        return errors.Length == 0
            ? null
            : Invalid(string.Join(" ", errors.Select(issue => issue.Message)));
    }

    private DefinitionStoreError? ValidateFileProviderProfile(FileProviderProfile definition)
    {
        var duplicate = ValidateName(Snapshot.FileProviderProfiles, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (definition.Configuration is not FileProviderConfiguration.Sftp sftp)
        {
            return null;
        }

        var connection = Snapshot.Connections
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == sftp.ConnectionId);
        return connection?.Endpoint is ConnectionEndpoint.Ssh
            ? null
            : new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "An SFTP provider requires an existing SSH connection profile.");
    }

    private DefinitionStoreError? ValidateQuickTerminalSettings(QuickTerminalSettings definition) =>
        ValidateName(Snapshot.QuickTerminalSettings, definition);

    private DefinitionStoreError? ValidateAiProviderProfile(AiProviderProfile definition)
    {
        var duplicate = ValidateName(Snapshot.AiProviderProfiles, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (!definition.IsEnabled
            && IsAiProviderPolicyDependency(definition.Id.Value))
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "This AI-provider profile cannot be disabled while a saved screen or workspace agent policy references it.");
        }

        return Snapshot.AiProviderProfiles.Any(item =>
            item.Value.Key != definition.Key
            && item.Value.Order == definition.Order)
            ? Invalid("Each AI provider must have a distinct fallback order.")
            : null;
    }

    private DefinitionStoreError? ValidateMcpServerProfile(McpServerProfile definition) =>
        ValidateName(Snapshot.McpServerProfiles, definition);

    private static DefinitionStoreError? ValidateName<TDefinition>(
        IReadOnlyList<StoredDefinition<TDefinition>> definitions,
        TDefinition candidate)
        where TDefinition : IDurableDefinition =>
        definitions.Any(item =>
            item.Value.Key != candidate.Key
            && string.Equals(item.Value.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            ? Invalid("A definition with this name already exists.")
            : null;

    private async ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> RefreshCoreAsync(
        CancellationToken cancellationToken)
    {
        var connectionsTask = _connections.ListAsync(cancellationToken).AsTask();
        var layoutsTask = _layouts.ListAsync(cancellationToken).AsTask();
        var screensTask = _screens.ListAsync(cancellationToken).AsTask();
        var workspacesTask = _workspaces.ListAsync(cancellationToken).AsTask();
        var themesTask = _themes.ListAsync(cancellationToken).AsTask();
        var terminalsTask = _terminalProfiles.ListAsync(cancellationToken).AsTask();
        var keymapsTask = _keymaps.ListAsync(cancellationToken).AsTask();
        var fileProvidersTask = _fileProviderProfiles.ListAsync(cancellationToken).AsTask();
        var aiProvidersTask = _aiProviderProfiles.ListAsync(cancellationToken).AsTask();
        var mcpServersTask = _mcpServerProfiles.ListAsync(cancellationToken).AsTask();
        var quickTerminalTask = _quickTerminalSettings.ListAsync(cancellationToken).AsTask();
        await Task.WhenAll(
                connectionsTask,
                layoutsTask,
                screensTask,
                workspacesTask,
                themesTask,
                terminalsTask,
                keymapsTask,
                fileProvidersTask,
                aiProvidersTask,
                mcpServersTask,
                quickTerminalTask)
            .ConfigureAwait(false);

        var errors = new DefinitionStoreError?[]
        {
            connectionsTask.Result.Error,
            layoutsTask.Result.Error,
            screensTask.Result.Error,
            workspacesTask.Result.Error,
            themesTask.Result.Error,
            terminalsTask.Result.Error,
            keymapsTask.Result.Error,
            fileProvidersTask.Result.Error,
            aiProvidersTask.Result.Error,
            mcpServersTask.Result.Error,
            quickTerminalTask.Result.Error,
        };
        var error = errors.FirstOrDefault(item => item is not null);
        if (error is not null)
        {
            return DefinitionStoreResult<DefinitionCatalogSnapshot>.Failure(error);
        }

        var snapshot = new DefinitionCatalogSnapshot(
            connectionsTask.Result.Value!,
            layoutsTask.Result.Value!,
            screensTask.Result.Value!,
            workspacesTask.Result.Value!,
            themesTask.Result.Value!,
            terminalsTask.Result.Value!,
            keymapsTask.Result.Value!,
            fileProvidersTask.Result.Value!,
            quickTerminalTask.Result.Value!)
        {
            AiProviderProfiles = aiProvidersTask.Result.Value!,
            McpServerProfiles = mcpServersTask.Result.Value!,
        };
        Volatile.Write(ref _snapshot, snapshot);
        return DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(snapshot);
    }

    private async ValueTask<DefinitionStoreResult<Unit>> SeedDefaultsAsync(
        CancellationToken cancellationToken)
    {
        foreach (var keymap in BuiltInKeymaps.All)
        {
            if (Snapshot.Keymaps.Any(item => item.Value.Id == keymap.Id))
            {
                continue;
            }

            var saved = await _keymaps.SaveAsync(keymap, null, cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (!Snapshot.Themes.Any(item => item.Value.Id == ThemePreference.Default.Id))
        {
            var saved = await _themes.SaveAsync(
                    ThemePreference.Default,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (Snapshot.TerminalProfiles.Count == 0)
        {
            var terminal = CreateDefaultTerminalProfile();
            var saved = await _terminalProfiles.SaveAsync(terminal, null, cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (Snapshot.QuickTerminalSettings.Count == 0)
        {
            var saved = await _quickTerminalSettings.SaveAsync(
                    QuickTerminalSettings.Default,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (Snapshot.Connections.Count != 0
            || Snapshot.Layouts.Count != 0
            || Snapshot.Screens.Count != 0
            || Snapshot.Workspaces.Count != 0)
        {
            return DefinitionStoreResult<Unit>.Success(Unit.Value);
        }

        var defaults = CreateFirstRunDefinitions();
        var connectionResult = await _connections.SaveAsync(
                defaults.Connection,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!connectionResult.IsSuccess)
        {
            return DefinitionStoreResult<Unit>.Failure(connectionResult.Error!);
        }

        var layoutResult = await _layouts.SaveAsync(defaults.Layout, null, cancellationToken)
            .ConfigureAwait(false);
        if (!layoutResult.IsSuccess)
        {
            return DefinitionStoreResult<Unit>.Failure(layoutResult.Error!);
        }

        var screenResult = await _screens.SaveAsync(defaults.Screen, null, cancellationToken)
            .ConfigureAwait(false);
        if (!screenResult.IsSuccess)
        {
            return DefinitionStoreResult<Unit>.Failure(screenResult.Error!);
        }

        var workspaceResult = await _workspaces.SaveAsync(
                defaults.Workspace,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return workspaceResult.IsSuccess
            ? DefinitionStoreResult<Unit>.Success(Unit.Value)
            : DefinitionStoreResult<Unit>.Failure(workspaceResult.Error!);
    }

    private static TerminalProfile CreateDefaultTerminalProfile()
    {
        var keymap = OperatingSystem.IsMacOS()
            ? BuiltInKeymaps.MacOsTerminalId
            : OperatingSystem.IsWindows()
                ? BuiltInKeymaps.WindowsTerminalId
                : BuiltInKeymaps.LinuxTerminalId;
        return new TerminalProfile(
            new TerminalProfileId("builtin.terminal.default"),
            "Default terminal",
            "JetBrains Mono",
            14,
            1.4,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            100_000,
            TerminalPalette.GhostShellDark,
            keymap);
    }

    private static FirstRunDefinitions CreateFirstRunDefinitions()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("builtin.local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local terminal",
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable,
            ["local"]);
        var slotId = new LayoutSlotId("main");
        var layout = new LayoutDefinition(
            new LayoutId("builtin.single-panel"),
            LayoutDefinition.CurrentSchemaVersion,
            "Single panel",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    slotId,
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(320, 180)),
            ]);
        var screen = new ScreenDefinition(
            new ScreenId("builtin.local-terminal"),
            ScreenDefinition.CurrentSchemaVersion,
            "Local terminal",
            "A local shell using the default terminal profile.",
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("terminal"),
                    slotId,
                    ScreenPanelKind.Terminal,
                    "Local terminal",
                    connection.Id,
                    PanelStartupBehavior.None),
            ],
            ["local"]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("default"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Local",
            "Your local GhostSHELL workspace.",
            "#B8793A",
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("local-connection"),
                    connection.Id),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("local-screen"),
                    screen.Id),
            ]);
        return new FirstRunDefinitions(connection, layout, screen, workspace);
    }

    private static DefinitionStoreError MissingWorkspaceDependency(string kind, string id) =>
        new(
            DefinitionStoreErrorCode.DependencyConflict,
            $"The workspace references missing {kind} '{id}'.");

    private static DefinitionStoreError Invalid(DefinitionValidationResult validation) =>
        Invalid(string.Join(" ", validation.Issues.Select(issue => issue.Message)));

    private static DefinitionStoreError Invalid(string message) =>
        new(DefinitionStoreErrorCode.InvalidDefinition, message);

    private sealed record FirstRunDefinitions(
        ConnectionProfile Connection,
        LayoutDefinition Layout,
        ScreenDefinition Screen,
        WorkspaceDefinition Workspace);
}
