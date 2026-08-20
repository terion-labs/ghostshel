using System.Text;
using DotNet.Testcontainers.Builders;
using GhostShell.Application;
using GhostShell.Redis;
using StackExchange.Redis;

namespace GhostShell.Databases.IntegrationTests;

public sealed class RedisDatabasePanelIntegrationTests
{
    [RedisIntegrationFact]
    public async Task RedisSessionBrowsesMutatesAndReceivesPubSub()
    {
        const ushort redisPort = 6379;
        var cancellationToken = CancellationToken.None;
        var container = new ContainerBuilder("redis:8.2-alpine")
            .WithPortBinding(redisPort, assignRandomHostPort: true)
            .WithCreateParameterModifier(ContainerDatabaseProviderCase.BindPublishedPortsToLoopback)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(redisPort))
            .Build();
        await using (container.ConfigureAwait(false))
        {
            await container.StartAsync(cancellationToken);
            var connectionString =
                $"{container.Hostname}:{container.GetMappedPublicPort(redisPort)},abortConnect=false";
            var factory = new RedisPanelSessionFactory();
            await using var session = await factory.OpenAsync(
                connectionString,
                tunnel: null,
                cancellationToken);

            Assert.StartsWith("8.", session.Facts.Version, StringComparison.Ordinal);
            Assert.Equal(RedisTopologyKind.Standalone, session.Facts.Topology);
            Assert.Equal(RedisLogicalDatabaseMode.Selectable, session.Facts.LogicalDatabases);
            Assert.True(session.Facts.SearchAvailable);
            Assert.True(session.Facts.JsonAvailable);
            Assert.True(session.Facts.TimeSeriesAvailable);

            await session.SelectDatabaseAsync(1, cancellationToken);
            await session.SetStringAsync(Key("ghostshell:string"), "hello redis", TimeSpan.FromMinutes(5), cancellationToken);
            await session.SetHashFieldAsync(Key("ghostshell:hash"), "name", "Ada", cancellationToken);
            await session.AppendListValueAsync(Key("ghostshell:list"), "first", cancellationToken);
            await session.AddSetValueAsync(Key("ghostshell:set"), "member", cancellationToken);
            await session.AddSortedSetValueAsync(Key("ghostshell:zset"), "ranked", 42, cancellationToken);
            await session.AddStreamEntryAsync(Key("ghostshell:stream"), "event", "created", cancellationToken);
            await session.SetJsonAsync(Key("ghostshell:json"), "{\"name\":\"Ada\",\"active\":true}", cancellationToken);
            await session.AddTimeSeriesSampleAsync(Key("ghostshell:timeseries"), 12.5, cancellationToken);

            var keys = await ScanAllAsync(session, "ghostshell:*", cancellationToken);
            Assert.Equal(8, keys.Count);
            var stringSnapshot = await session.ReadKeyAsync(Key("ghostshell:string"), 100, cancellationToken);
            Assert.Equal("string", stringSnapshot.Summary.Type);
            Assert.Equal("hello redis", Assert.Single(stringSnapshot.Entries).Value);
            Assert.NotNull(stringSnapshot.Summary.TimeToLive);
            var jsonSnapshot = await session.ReadKeyAsync(Key("ghostshell:json"), 500, cancellationToken);
            Assert.Equal("json", jsonSnapshot.Summary.Type);
            Assert.Contains("\"name\":\"Ada\"", Assert.Single(jsonSnapshot.Entries).Value, StringComparison.Ordinal);
            var largeJson = $$"""{"payload":"{{new string('x', 20_000)}}"}""";
            await session.SetJsonAsync(Key("ghostshell:large-json"), largeJson, cancellationToken);
            var boundedJson = await session.ReadKeyAsync(
                Key("ghostshell:large-json"),
                64,
                cancellationToken);
            Assert.True(boundedJson.Truncated);
            Assert.Equal(Encoding.UTF8.GetByteCount(largeJson), boundedJson.Length);
            Assert.InRange(Assert.Single(boundedJson.Entries).Value.Length, 1, 64);
            var timeSeriesSnapshot = await session.ReadKeyAsync(Key("ghostshell:timeseries"), 100, cancellationToken);
            Assert.Equal("timeseries", timeSeriesSnapshot.Summary.Type);
            Assert.Equal("12.5", Assert.Single(timeSeriesSnapshot.Entries).Value);

            var received = new TaskCompletionSource<RedisPubSubMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.MessageReceived += (_, message) => received.TrySetResult(message);
            var subscription = new RedisSubscription(RedisSubscriptionKind.Channel, "ghostshell:events");
            await session.SubscribeAsync(subscription, cancellationToken);
            await session.PublishAsync(subscription.Name, "panel-ready", sharded: false, cancellationToken);
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal("panel-ready", message.Payload);
            await session.UnsubscribeAsync(subscription, cancellationToken);

            await session.SelectDatabaseAsync(0, cancellationToken);
            await using (var seed = await ConnectionMultiplexer.ConnectAsync(connectionString))
            {
                var database = seed.GetDatabase();
                await database.ExecuteAsync(
                    "FT.CREATE",
                    "ghostshell-index",
                    "ON",
                    "HASH",
                    "PREFIX",
                    1,
                    "ghostshell:document:",
                    "SCHEMA",
                    "title",
                    "TEXT",
                    "category",
                    "TAG");
                await database.HashSetAsync(
                    "ghostshell:document:1",
                    [
                        new HashEntry("title", "Redis panel integration"),
                        new HashEntry("category", "database"),
                    ]);
            }

            var indexes = await session.ListSearchIndexesAsync(cancellationToken);
            Assert.Contains(indexes, index => string.Equals(index.Name, "ghostshell-index", StringComparison.Ordinal));
            var search = await WaitForSearchResultAsync(session, cancellationToken);
            Assert.Equal(1, search.Total);
            Assert.Contains(search.Values, value => string.Equals(value.Identity, "ghostshell:document:1", StringComparison.Ordinal));

            await session.SelectDatabaseAsync(1, cancellationToken);

            // Each collection addresses its entries differently, and a list not
            // at all except by rewriting the element it is at, so removal is
            // exercised against a real server for every type that offers it.
            await session.SetHashFieldAsync(Key("ghostshell:hash"), "city", "London", cancellationToken);
            await session.AppendListValueAsync(Key("ghostshell:list"), "second", cancellationToken);
            await session.AddSetValueAsync(Key("ghostshell:set"), "other", cancellationToken);
            await session.AddSortedSetValueAsync(Key("ghostshell:zset"), "runner-up", 7, cancellationToken);

            await RemoveFirstEntryAsync(session, "ghostshell:hash", "hash", cancellationToken);
            Assert.Equal(
                ["city"],
                (await session.ReadKeyAsync(Key("ghostshell:hash"), 100, cancellationToken))
                    .Entries.Select(entry => entry.Field), StringComparer.Ordinal);

            await RemoveFirstEntryAsync(session, "ghostshell:list", "list", cancellationToken);
            Assert.Equal(
                ["second"],
                (await session.ReadKeyAsync(Key("ghostshell:list"), 100, cancellationToken))
                    .Entries.Select(entry => entry.Value), StringComparer.Ordinal);

            await session.AppendListValueAsync(Key("ghostshell:list"), "third", cancellationToken);
            var staleListSnapshot = await session.ReadKeyAsync(
                Key("ghostshell:list"),
                100,
                cancellationToken);
            await using (var competing = await ConnectionMultiplexer.ConnectAsync(connectionString))
            {
                await competing.GetDatabase(1).ListLeftPushAsync("ghostshell:list", "inserted");
            }

            Assert.Equal(
                RedisEntryRemovalOutcome.Stale,
                await session.RemoveEntryAsync(
                    Key("ghostshell:list"),
                    "list",
                    staleListSnapshot.Entries[0],
                    cancellationToken));
            var reconciledList = await session.ReadKeyAsync(
                Key("ghostshell:list"),
                100,
                cancellationToken);
            Assert.Equal(
                ["inserted", "second", "third"],
                reconciledList.Entries.Select(entry => entry.Value),
                StringComparer.Ordinal);
            Assert.DoesNotContain(
                reconciledList.Entries,
                entry => entry.Value.Contains("__ghostshell_removed_", StringComparison.Ordinal));

            await session.AppendListValueAsync(Key("ghostshell:duplicates"), "same", cancellationToken);
            await session.AppendListValueAsync(Key("ghostshell:duplicates"), "same", cancellationToken);
            var duplicates = await session.ReadKeyAsync(
                Key("ghostshell:duplicates"),
                100,
                cancellationToken);
            Assert.Equal(
                RedisEntryRemovalOutcome.Removed,
                await session.RemoveEntryAsync(
                    Key("ghostshell:duplicates"),
                    "list",
                    duplicates.Entries[1],
                    cancellationToken));
            Assert.Equal(
                ["same"],
                (await session.ReadKeyAsync(Key("ghostshell:duplicates"), 100, cancellationToken))
                    .Entries.Select(entry => entry.Value),
                StringComparer.Ordinal);

            var binaryValue = new byte[] { 0x00, 0xFF, 0x01 };
            await using (var competing = await ConnectionMultiplexer.ConnectAsync(connectionString))
            {
                await competing.GetDatabase(1).ListRightPushAsync("ghostshell:binary-list", binaryValue);
            }

            var binaryList = await session.ReadKeyAsync(
                Key("ghostshell:binary-list"),
                100,
                cancellationToken);
            var binaryEntry = Assert.Single(binaryList.Entries);
            Assert.Equal(binaryValue, binaryEntry.RawValue);
            Assert.Equal(
                RedisEntryRemovalOutcome.Removed,
                await session.RemoveEntryAsync(
                    Key("ghostshell:binary-list"),
                    "list",
                    binaryEntry,
                    cancellationToken));

            var cancellationSnapshot = await session.ReadKeyAsync(
                Key("ghostshell:duplicates"),
                100,
                cancellationToken);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.RemoveEntryAsync(
                    Key("ghostshell:duplicates"),
                    "list",
                    cancellationSnapshot.Entries[0],
                    cancelled.Token));
            Assert.Equal(
                ["same"],
                (await session.ReadKeyAsync(Key("ghostshell:duplicates"), 100, cancellationToken))
                    .Entries.Select(entry => entry.Value),
                StringComparer.Ordinal);

            await RemoveFirstEntryAsync(session, "ghostshell:set", "set", cancellationToken);
            Assert.Single((await session.ReadKeyAsync(Key("ghostshell:set"), 100, cancellationToken)).Entries);

            await RemoveFirstEntryAsync(session, "ghostshell:zset", "zset", cancellationToken);
            Assert.Single((await session.ReadKeyAsync(Key("ghostshell:zset"), 100, cancellationToken)).Entries);

            // A stream entry is the whole field set at one id.
            await RemoveFirstEntryAsync(session, "ghostshell:stream", "stream", cancellationToken);
            Assert.Empty((await session.ReadKeyAsync(Key("ghostshell:stream"), 100, cancellationToken)).Entries);

            Assert.True(await session.DeleteKeyAsync(Key("ghostshell:string"), cancellationToken));
            var deleted = await session.ReadKeyAsync(Key("ghostshell:string"), 10, cancellationToken);
            Assert.Equal("none", deleted.Summary.Type);
        }
    }

    private static async Task RemoveFirstEntryAsync(
        IRedisPanelSession session,
        string name,
        string type,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.ReadKeyAsync(Key(name), 100, cancellationToken);
        Assert.Equal(
            RedisEntryRemovalOutcome.Removed,
            await session.RemoveEntryAsync(Key(name), type, snapshot.Entries[0], cancellationToken));
    }

    private static RedisKeyReference Key(string value) =>
        new(value, System.Text.Encoding.UTF8.GetBytes(value));

    private static async Task<IReadOnlyList<RedisKeySummary>> ScanAllAsync(
        IRedisPanelSession session,
        string pattern,
        CancellationToken cancellationToken)
    {
        var keys = new Dictionary<string, RedisKeySummary>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await session.ScanKeysAsync(pattern, cursor, 20, cancellationToken);
            foreach (var key in page.Keys)
            {
                keys[Convert.ToBase64String(key.Key.Bytes)] = key;
            }

            cursor = page.NextCursor;
            if (page.IsComplete)
            {
                break;
            }
        }
        while (true);
        return [.. keys.Values];
    }

    private static async Task<RedisSearchResult> WaitForSearchResultAsync(
        IRedisPanelSession session,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await session.SearchAsync(
                "ghostshell-index",
                "@title:(Redis panel)",
                10,
                cancellationToken);
            if (result.Total > 0)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return await session.SearchAsync("ghostshell-index", "@title:(Redis panel)", 10, cancellationToken);
    }
}
