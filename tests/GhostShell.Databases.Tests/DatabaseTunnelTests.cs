using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Databases;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseTunnelTests
{
    [Theory]
    [InlineData(
        "postgres",
        "Host=db.internal;Port=5433;Database=app",
        "db.internal",
        5433)]
    [InlineData(
        "cockroach",
        "Host=db.internal;Database=app",
        "db.internal",
        5432)]
    [InlineData(
        "mysql",
        "Server=db.internal;Port=3307;Database=app",
        "db.internal",
        3307)]
    [InlineData(
        "sqlserver",
        "Server=db.internal,14330;Database=app",
        "db.internal",
        14330)]
    [InlineData(
        "sqlserver",
        "Server=db.internal;Database=app",
        "db.internal",
        1433)]
    [InlineData(
        "oracle",
        "Data Source=db.internal:1522/FREEPDB1;User Id=app",
        "db.internal",
        1522)]
    [InlineData(
        "oracle",
        "Data Source=db.internal/FREEPDB1;User Id=app",
        "db.internal",
        1521)]
    [InlineData(
        "firebird",
        "DataSource=db.internal;Port=3051;Database=/srv/app.fdb",
        "db.internal",
        3051)]
    [InlineData(
        "clickhouse",
        "Host=db.internal;Port=9004;Database=default",
        "db.internal",
        9004)]
    public void Network_drivers_expose_their_endpoint(
        string driverId,
        string connectionString,
        string expectedHost,
        int expectedPort)
    {
        var driver = Driver(driverId);

        var endpoint = driver.GetEndpoint(connectionString);

        Assert.NotNull(endpoint);
        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(expectedPort, endpoint.Port);

        // The rewritten string points at the forward and keeps everything else.
        var rewritten = driver.RewriteEndpoint(connectionString, "127.0.0.1", 15432);
        var forwarded = driver.GetEndpoint(rewritten);
        Assert.NotNull(forwarded);
        Assert.Equal("127.0.0.1", forwarded.Host);
        Assert.Equal(15432, forwarded.Port);
    }

    [Fact]
    public void Oracle_rewrite_keeps_the_service_name()
    {
        var driver = Driver("oracle");

        var rewritten = driver.RewriteEndpoint(
            "Data Source=db.internal:1522/FREEPDB1;User Id=app",
            "127.0.0.1",
            15210);

        Assert.Contains("127.0.0.1:15210/FREEPDB1", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void File_engines_have_no_endpoint_and_refuse_tunneling()
    {
        Assert.Null(Driver("sqlite").GetEndpoint("Data Source=/tmp/x.db"));
        Assert.Null(Driver("duckdb").GetEndpoint("Data Source=/tmp/x.duckdb"));
        Assert.Throws<InvalidOperationException>(() =>
            Driver("sqlite").RewriteEndpoint("Data Source=/tmp/x.db", "127.0.0.1", 1));
    }

    [Fact]
    public async Task Tunnel_request_for_a_file_engine_fails_before_connecting()
    {
        var factory = new RecordingTunnelFactory();
        var client = new DatabasePanelClient(BuiltInDatabaseDrivers.All, factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListTablesAsync(
                "sqlite",
                "Data Source=/tmp/x.db",
                SshProfile(),
                CancellationToken.None));

        Assert.Contains("cannot be tunneled", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task Tunneled_query_rewrites_the_endpoint_and_reuses_the_forward()
    {
        // SQLite listens nowhere, so the "forwarded" endpoint is validated by a
        // driver stub that records what the client asked it to connect to.
        var driver = new RecordingDriver();
        var factory = new RecordingTunnelFactory();
        var client = new DatabasePanelClient([driver], factory);
        var tunnel = SshProfile();

        _ = await client.ListTablesAsync("recording", "Host=db.internal;Port=9;", tunnel, CancellationToken.None);
        _ = await client.ListTablesAsync("recording", "Host=db.internal;Port=9;", tunnel, CancellationToken.None);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(("db.internal", 9), factory.LastTarget);
        Assert.Equal("Host=127.0.0.1;Port=45001;", driver.LastConnectionString);
    }

    private static IDatabaseDriver Driver(string id) =>
        BuiltInDatabaseDrivers.All.Single(driver => driver.Descriptor.Id == id);

    private static ConnectionProfile SshProfile() => new(
        new ConnectionId("bastion"),
        ConnectionProfile.CurrentSchemaVersion,
        "bastion",
        new ConnectionEndpoint.Ssh("bastion.example.test", username: "ops"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);

    private sealed class RecordingTunnelFactory : IDatabaseTunnelFactory
    {
        public int OpenCount { get; private set; }

        public (string Host, int Port)? LastTarget { get; private set; }

        public ValueTask<IDatabaseTunnelLease> OpenAsync(
            ConnectionProfile connection,
            string targetHost,
            int targetPort,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            LastTarget = (targetHost, targetPort);
            return ValueTask.FromResult<IDatabaseTunnelLease>(new Lease());
        }

        private sealed class Lease : IDatabaseTunnelLease
        {
            public int LocalPort => 45001;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDriver : IDatabaseDriver
    {
        public string? LastConnectionString { get; private set; }

        public DatabaseDriverDescriptor Descriptor { get; } = new(
            "recording",
            "Recording",
            "Host=…");

        public System.Data.Common.DbConnection CreateConnection(string connectionString)
        {
            LastConnectionString = connectionString;
            // In-memory SQLite lets the client run its full pipeline without a
            // server; only the recorded connection string matters here.
            return new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        }

        public string ListTablesSql =>
            "SELECT name, type FROM sqlite_master WHERE type IN ('table','view');";

        public string QuoteIdentifier(string identifier) => identifier;

        public string BuildPreviewQuery(string tableName, int limit) =>
            $"SELECT * FROM {tableName} LIMIT {limit};";

        public DatabaseEndpoint? GetEndpoint(string connectionString)
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder
            {
                ConnectionString = connectionString,
            };
            return builder.TryGetValue("Host", out var host)
                ? new DatabaseEndpoint(
                    (string)host,
                    builder.TryGetValue("Port", out var port)
                        ? int.Parse((string)port, System.Globalization.CultureInfo.InvariantCulture)
                        : 0)
                : null;
        }

        public string RewriteEndpoint(string connectionString, string host, int port) =>
            $"Host={host};Port={port};";
    }
}
