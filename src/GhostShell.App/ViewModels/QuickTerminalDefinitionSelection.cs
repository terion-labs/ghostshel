using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

internal sealed record QuickTerminalDefinitionSelection(
    StoredDefinition<ConnectionProfile>? Connection,
    StoredDefinition<TerminalProfile>? TerminalProfile,
    StoredDefinition<KeymapProfile>? TerminalKeymap)
{
    private static readonly ConnectionId BuiltInLocalConnectionId = new("builtin.local");
    private static readonly TerminalProfileId BuiltInTerminalProfileId = new("builtin.terminal.default");

    public QuickTerminalDefinitionSignature Signature => new(
        Connection?.Value.Id,
        Connection?.Revision,
        TerminalProfile?.Value.Id,
        TerminalProfile?.Revision,
        TerminalKeymap?.Value.Id,
        TerminalKeymap?.Revision);

    public static QuickTerminalDefinitionSelection Resolve(DefinitionCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var connection = snapshot.Connections
            .Where(item => item.Value.Endpoint is ConnectionEndpoint.Local)
            .OrderByDescending(item => item.Value.Id == BuiltInLocalConnectionId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        var terminalProfile = snapshot.TerminalProfiles
            .OrderByDescending(item => item.Value.Id == BuiltInTerminalProfileId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        var terminalKeymap = terminalProfile is null
            ? null
            : ResolveKeymap(snapshot, terminalProfile.Value.KeymapId);
        return new QuickTerminalDefinitionSelection(connection, terminalProfile, terminalKeymap);
    }

    private static StoredDefinition<KeymapProfile>? ResolveKeymap(
        DefinitionCatalogSnapshot snapshot,
        KeymapProfileId keymapId)
    {
        var stored = snapshot.Keymaps.FirstOrDefault(item => item.Value.Id == keymapId);
        if (stored is not null)
        {
            return stored.Value.Layer == KeymapLayer.Terminal ? stored : null;
        }

        var builtIn = BuiltInKeymaps.All.FirstOrDefault(item => item.Id == keymapId);
        return builtIn?.Layer == KeymapLayer.Terminal
            ? new StoredDefinition<KeymapProfile>(
                builtIn,
                Revision: 0,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch)
            : null;
    }
}

internal readonly record struct QuickTerminalDefinitionSignature(
    ConnectionId? ConnectionId,
    long? ConnectionRevision,
    TerminalProfileId? TerminalProfileId,
    long? TerminalProfileRevision,
    KeymapProfileId? TerminalKeymapId,
    long? TerminalKeymapRevision);

internal sealed class QuickTerminalDefinitionTracker
{
    private QuickTerminalDefinitionSignature _current;

    public QuickTerminalDefinitionTracker(DefinitionCatalogSnapshot initialSnapshot)
    {
        _current = QuickTerminalDefinitionSelection.Resolve(initialSnapshot).Signature;
    }

    public bool Update(DefinitionCatalogSnapshot snapshot)
    {
        var next = QuickTerminalDefinitionSelection.Resolve(snapshot).Signature;
        if (next == _current)
        {
            return false;
        }

        _current = next;
        return true;
    }
}
