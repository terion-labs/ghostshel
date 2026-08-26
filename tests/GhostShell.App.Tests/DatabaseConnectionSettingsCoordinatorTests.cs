using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class DatabaseConnectionSettingsCoordinatorTests
{
    [Fact]
    public async Task Save_strips_the_password_and_forwards_a_null_create_revision()
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        var catalogProxy = (CatalogProxy)(object)catalog;
        var database = DispatchProxy.Create<IDatabaseConnectionCatalog, DatabaseCatalogProxy>();
        var databaseProxy = (DatabaseCatalogProxy)(object)database;
        var vault = DispatchProxy.Create<ISecretVault, VaultProxy>();
        var errors = new List<string>();
        var coordinator = new DatabaseConnectionSettingsCoordinator(
            catalog,
            database,
            vault,
            errors.Add,
            errors.Add);

        var saved = await coordinator.SaveDatabaseConnectionAsync(
            existingId: null,
            "  Production  ",
            "postgres",
            new DatabaseConnectionDetails(
                Host: "db.internal",
                Database: "app",
                Password: "session-only"),
            storePassword: false,
            tunnelConnectionId: null,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal("Production", catalogProxy.SavedProfile?.Name);
        Assert.Null(catalogProxy.ExpectedRevision);
        Assert.Null(databaseProxy.BuiltDetails?.Password);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Missing_database_catalog_reports_an_error_before_persistence()
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        var catalogProxy = (CatalogProxy)(object)catalog;
        var vault = DispatchProxy.Create<ISecretVault, VaultProxy>();
        var errors = new List<string>();
        var coordinator = new DatabaseConnectionSettingsCoordinator(
            catalog,
            databaseConnectionCatalog: null,
            vault,
            errors.Add,
            errors.Add);

        var saved = await coordinator.SaveDatabaseConnectionAsync(
            existingId: null,
            "Production",
            "postgres",
            new DatabaseConnectionDetails(),
            storePassword: false,
            tunnelConnectionId: null,
            cancellationToken: CancellationToken.None);

        Assert.Null(saved);
        Assert.Null(catalogProxy.SavedProfile);
        Assert.Contains(errors, error => error.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    public class CatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot Snapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public DatabaseConnectionProfile? SavedProfile { get; private set; }

        public long? ExpectedRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => Snapshot,
                "add_Changed" or "remove_Changed" => null,
                nameof(IDefinitionCatalog.SaveDatabaseConnectionAsync) => Save(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object Save(object?[] args)
        {
            SavedProfile = (DatabaseConnectionProfile)args[0]!;
            ExpectedRevision = (long?)args[1];
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>>.Success(
                    new(
                        SavedProfile,
                        (ExpectedRevision ?? 0) + 1,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));
        }
    }

    public class DatabaseCatalogProxy : DispatchProxy
    {
        public DatabaseConnectionDetails? BuiltDetails { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Drivers" => Array.Empty<DatabaseDriverDescriptor>(),
                nameof(IDatabaseConnectionCatalog.BuildConnectionString) => Build(args!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private object Build(object?[] args)
        {
            BuiltDetails = (DatabaseConnectionDetails)args[1]!;
            return "Host=db.internal;Database=app";
        }
    }

    public class VaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
