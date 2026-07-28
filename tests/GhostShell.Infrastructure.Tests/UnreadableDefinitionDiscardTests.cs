using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

/// <summary>
/// While the definition schemas are still moving, a stored row the current build
/// cannot read is discarded rather than migrated. What must not happen is the
/// whole profile becoming unavailable because one row is outdated.
/// </summary>
public sealed class UnreadableDefinitionDiscardTests
{
    private static async Task WriteRawAsync(
        TemporaryDatabase temporary,
        DefinitionKind kind,
        string id,
        string name,
        int schemaVersion,
        string payloadJson)
    {
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO definitions (kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc)
            VALUES ($kind, $id, $schema, 1, $name, $payload, $now, $now);
            """;
        insert.Parameters.AddWithValue("$kind", kind.Value);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$schema", schemaVersion);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$payload", payloadJson);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UnixEpoch.ToString("O"));
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(TemporaryDatabase temporary, DefinitionKind kind)
    {
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM definitions WHERE kind = $kind;";
        count.Parameters.AddWithValue("$kind", kind.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task An_outdated_row_does_not_make_the_list_fail()
    {
        await using var temporary = TemporaryDatabase.Create();
        await WriteRawAsync(
            temporary,
            DefinitionKind.Theme,
            ThemePreference.Default.Id.Value,
            "Automatic",
            schemaVersion: 1,
            """{"id":{"value":"builtin.theme.automatic"},"schemaVersion":1,"name":"Automatic"}""");

        var listed = await new SqliteDefinitionRepository<ThemePreference>(
            temporary.Database,
            TimeProvider.System).ListAsync(CancellationToken.None);

        Assert.True(listed.IsSuccess, listed.Error?.Message);
        Assert.Empty(listed.Value!);
    }

    [Fact]
    public async Task An_outdated_row_is_removed_so_the_next_start_is_clean()
    {
        await using var temporary = TemporaryDatabase.Create();
        await WriteRawAsync(
            temporary,
            DefinitionKind.Theme,
            ThemePreference.Default.Id.Value,
            "Automatic",
            schemaVersion: 1,
            """{"id":{"value":"builtin.theme.automatic"},"schemaVersion":1,"name":"Automatic"}""");
        var repository = new SqliteDefinitionRepository<ThemePreference>(
            temporary.Database,
            TimeProvider.System);

        Assert.True((await repository.ListAsync(CancellationToken.None)).IsSuccess);

        Assert.Equal(0, await CountAsync(temporary, DefinitionKind.Theme));
    }

    [Fact]
    public async Task A_corrupt_payload_is_discarded_too()
    {
        await using var temporary = TemporaryDatabase.Create();
        await WriteRawAsync(
            temporary,
            DefinitionKind.Theme,
            ThemePreference.Default.Id.Value,
            "Automatic",
            ThemePreference.CurrentSchemaVersion,
            """{"not":"a theme"}""");

        var listed = await new SqliteDefinitionRepository<ThemePreference>(
            temporary.Database,
            TimeProvider.System).ListAsync(CancellationToken.None);

        Assert.True(listed.IsSuccess, listed.Error?.Message);
        Assert.Empty(listed.Value!);
        Assert.Equal(0, await CountAsync(temporary, DefinitionKind.Theme));
    }

    [Fact]
    public async Task Readable_rows_beside_an_unreadable_one_survive()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var keeper = DurableDefinitionFixtures.Layout(id: "keeper", name: "Keeper");
        Assert.True((await repository.SaveAsync(keeper, null, CancellationToken.None)).IsSuccess);
        await WriteRawAsync(
            temporary,
            DefinitionKind.Layout,
            "outdated",
            "Outdated",
            schemaVersion: 999,
            """{"schemaVersion":999}""");

        var listed = await repository.ListAsync(CancellationToken.None);

        Assert.True(listed.IsSuccess, listed.Error?.Message);
        var survivor = Assert.Single(listed.Value!);
        Assert.Equal(keeper.Id, survivor.Value.Id);
        Assert.Equal(1, await CountAsync(temporary, DefinitionKind.Layout));
    }

    [Fact]
    public async Task Discarding_leaves_other_kinds_untouched()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await layouts.SaveAsync(
            DurableDefinitionFixtures.Layout(),
            null,
            CancellationToken.None)).IsSuccess);
        await WriteRawAsync(
            temporary,
            DefinitionKind.Theme,
            ThemePreference.Default.Id.Value,
            "Automatic",
            schemaVersion: 1,
            """{"id":{"value":"builtin.theme.automatic"},"schemaVersion":1,"name":"Automatic"}""");

        Assert.True((await new SqliteDefinitionRepository<ThemePreference>(
            temporary.Database,
            TimeProvider.System).ListAsync(CancellationToken.None)).IsSuccess);

        Assert.Equal(0, await CountAsync(temporary, DefinitionKind.Theme));
        Assert.Equal(1, await CountAsync(temporary, DefinitionKind.Layout));
    }
}
