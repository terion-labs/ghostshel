using GhostShell.Core;

namespace GhostShell.Application;

public interface IDefinitionCatalog
{
    DefinitionCatalogSnapshot Snapshot { get; }

    event EventHandler? Changed;

    ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
        LayoutDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
        ScreenDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
        WorkspaceDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        ThemePreference definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        TerminalProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
        KeymapProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
        FileProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
        AiProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
        McpServerProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        QuickTerminalSettings definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken);
}
