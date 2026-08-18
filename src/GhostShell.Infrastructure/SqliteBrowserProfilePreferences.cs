using GhostShell.Application;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure;

/// <summary>
/// Live browser-profile sharing settings backed by one SQLite row. An
/// unavailable row falls back to the privacy-compatible product default and
/// never prevents the browser from starting.
/// </summary>
public sealed class SqliteBrowserProfilePreferences : IBrowserProfilePreferences
{
    private readonly GhostShellDatabase _database;
    private volatile BrowserProfileSettings _current = BrowserProfileSettings.Default;

    public SqliteBrowserProfilePreferences(GhostShellDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public BrowserProfileSettings Current => _current;

    public event EventHandler? Changed;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sharing
                FROM browser_profile_preference
                WHERE singleton_id = 1;
                """;
            var value = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            _current = value switch
            {
                0L => new BrowserProfileSettings(BrowserProfileSharing.Shared),
                1L => new BrowserProfileSettings(BrowserProfileSharing.PerWorkspace),
                _ => BrowserProfileSettings.Default,
            };
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            _current = BrowserProfileSettings.Default;
        }
    }

    public async ValueTask ApplyAsync(
        BrowserProfileSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await using var connection = await _database
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE browser_profile_preference
                SET sharing = $sharing
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue(
                "$sharing",
                settings.Sharing == BrowserProfileSharing.PerWorkspace ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Keep the live choice. The next successful edit persists it.
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException;
}
