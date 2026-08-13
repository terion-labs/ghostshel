using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Connection-definition operations shared by relational and non-relational
/// database runtimes. It deliberately excludes query operations: a connection
/// editor can describe Redis without pretending Redis is an ADO.NET driver.
/// </summary>
public interface IDatabaseConnectionCatalog
{
    IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    Task<DatabaseSessionInfo> DescribeSessionAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DatabaseSessionInfo());

    DatabaseConnectionDetails ParseConnectionDetails(
        string driverId,
        string connectionString);

    string BuildConnectionString(
        string driverId,
        DatabaseConnectionDetails details);
}
