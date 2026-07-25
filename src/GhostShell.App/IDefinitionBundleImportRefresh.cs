using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

/// <summary>
/// Reloads presentation-visible definitions after an atomic import has committed.
/// </summary>
public interface IDefinitionBundleImportRefresh
{
    ValueTask<DefinitionStoreResult<Unit>> ReloadAsync(CancellationToken cancellationToken);
}
