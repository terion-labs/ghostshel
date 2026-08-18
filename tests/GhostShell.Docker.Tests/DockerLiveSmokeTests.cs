using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docker;
using GhostShell.Infrastructure;

namespace GhostShell.Docker.Tests;

public sealed class DockerLiveSmokeTests
{
    [Fact]
    public async Task RemoteSnapshotAndVolumeUsageCanBeReadThroughTheProductionCommandRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GHOSTSHELL_RUN_DOCKER_SSH_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var host = Environment.GetEnvironmentVariable("GHOSTSHELL_DOCKER_SSH_HOST")
            ?? throw new InvalidOperationException("GHOSTSHELL_DOCKER_SSH_HOST is required.");
        var username = Environment.GetEnvironmentVariable("GHOSTSHELL_DOCKER_SSH_USERNAME")
            ?? throw new InvalidOperationException("GHOSTSHELL_DOCKER_SSH_USERNAME is required.");
        var profile = new ConnectionProfile(
            new ConnectionId("docker-ssh-smoke"),
            ConnectionProfile.CurrentSchemaVersion,
            "Docker SSH smoke",
            new ConnectionEndpoint.Ssh(host, username: username),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        var executor = new ConnectionCommandExecutor(new SshRuntime(profile));
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var snapshotResult = await client.ReadSnapshotAsync(profile, CancellationToken.None);
        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(
            snapshotResult).Value;
        Assert.NotEmpty(snapshot.Containers);
        Assert.NotEmpty(snapshot.Images);
        Assert.NotEmpty(snapshot.Volumes);
        Assert.NotEmpty(snapshot.Networks);

        var usageResult = await client.ReadVolumeUsageAsync(profile, CancellationToken.None);
        var usage = Assert.IsType<DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success>(
            usageResult).Value;
        Assert.NotEmpty(usage);
        Assert.Contains(usage, item => item.SizeBytes is > 0);
    }

    [Fact]
    public async Task VolumeUsageCanBeReadFromTheProductionCommandRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GHOSTSHELL_RUN_DOCKER_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var executor = new ConnectionCommandExecutor(new LocalRuntime());
        var client = new DockerEngineClient(executor, TimeProvider.System);

        var result = await client.ReadVolumeUsageAsync(
            BuiltInConnections.Local,
            CancellationToken.None);

        var usage = Assert.IsType<DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success>(
            result).Value;
        Assert.NotEmpty(usage);
        Assert.All(usage, item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
        Assert.Contains(usage, item => item.SizeBytes is > 0);
    }

    [Fact]
    public async Task SelectedContainerRootCanBeBrowsedThroughTheProductionCommandRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GHOSTSHELL_RUN_DOCKER_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var containerName = Environment.GetEnvironmentVariable(
            "GHOSTSHELL_DOCKER_SMOKE_CONTAINER") ?? "dosvit-grafana-1";
        var executor = new ConnectionCommandExecutor(new LocalRuntime());
        var client = new DockerEngineClient(executor, TimeProvider.System);
        var snapshotResult = await client.ReadSnapshotAsync(
            BuiltInConnections.Local,
            CancellationToken.None);
        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(
            snapshotResult).Value;
        var container = Assert.Single(snapshot.Containers, item => string.Equals(item.Name, containerName, StringComparison.Ordinal));

        var result = await client.ListFilesAsync(
            BuiltInConnections.Local,
            new DockerResourceReference(
                DockerResourceKind.Container,
                container.Id,
                container.Name),
            "/",
            CancellationToken.None);

        var listing = Assert.IsType<DockerResult<DockerFileListing>.Success>(result).Value;
        Assert.Equal("/", listing.Path);
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, ".dockerenv", StringComparison.Ordinal));
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, "etc", StringComparison.Ordinal) && entry.Kind == DockerFileKind.Directory);
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, "usr", StringComparison.Ordinal) && entry.Kind == DockerFileKind.Directory);
        Assert.Contains(listing.Entries, entry => string.Equals(entry.Name, "var", StringComparison.Ordinal) && entry.Kind == DockerFileKind.Directory);

        var etcResult = await client.ListFilesAsync(
            BuiltInConnections.Local,
            listing.Resource,
            "/etc",
            CancellationToken.None);
        var etc = Assert.IsType<DockerResult<DockerFileListing>.Success>(etcResult).Value;
        Assert.Contains(etc.Entries, entry => string.Equals(entry.Name, "passwd", StringComparison.Ordinal));

        var statResult = await client.StatFileAsync(
            BuiltInConnections.Local,
            listing.Resource,
            "/etc/passwd",
            CancellationToken.None);
        var stat = Assert.IsType<DockerResult<DockerFileEntry>.Success>(statResult).Value;
        Assert.Equal("passwd", stat.Name);
        Assert.Equal(DockerFileKind.File, stat.Kind);
        Assert.True(stat.Size > 0);

        var previewResult = await client.ReadFileAsync(
            BuiltInConnections.Local,
            listing.Resource,
            "/etc/passwd",
            256 * 1024,
            CancellationToken.None);
        var preview = Assert.IsType<DockerResult<DockerFileContent>.Success>(
            previewResult).Value;
        Assert.False(preview.Content.IsEmpty);
        Assert.Contains("root:", System.Text.Encoding.UTF8.GetString(preview.Content.Span));
    }

    [Fact]
    public async Task SelectedContainerLogsAcceptOneTimestampCursorThroughProductionRuntime()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GHOSTSHELL_RUN_DOCKER_SMOKE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var containerName = Environment.GetEnvironmentVariable(
            "GHOSTSHELL_DOCKER_SMOKE_CONTAINER") ?? "dosvit-grafana-1";
        var client = new DockerEngineClient(
            new ConnectionCommandExecutor(new LocalRuntime()),
            TimeProvider.System);
        var snapshot = Assert.IsType<DockerResult<DockerEngineSnapshot>.Success>(
            await client.ReadSnapshotAsync(
                BuiltInConnections.Local,
                CancellationToken.None)).Value;
        var container = Assert.Single(snapshot.Containers, item => string.Equals(item.Name, containerName, StringComparison.Ordinal));

        var result = await client.ReadContainerLogsAsync(
            BuiltInConnections.Local,
            new DockerContainerLogRequest(
                container.Id,
                Limit: 30,
                SinceTimestamp: DateTimeOffset.UtcNow
                    .AddHours(-1)
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            CancellationToken.None);

        var page = Assert.IsType<DockerResult<DockerContainerLogPage>.Success>(result).Value;
        Assert.InRange(page.Lines.Count, 0, 30);
    }

    private sealed class LocalRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(null),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SshRuntime(ConnectionProfile profile) : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile requestedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(profile.Id, requestedProfile.Id);
            var endpoint = Assert.IsType<ConnectionEndpoint.Ssh>(profile.Endpoint);
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Ssh,
                    new TerminalLaunchRequest(
                        null,
                        "ssh",
                        [
                            "-p", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "-o", "StrictHostKeyChecking=yes",
                            "-o", "AddKeysToAgent=yes",
                            "-tt",
                            "-l", endpoint.Username!,
                            "--", endpoint.Host,
                        ]),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.Strict,
                    ConnectionReconnectMode.BoundedBackoff)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile requestedProfile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
