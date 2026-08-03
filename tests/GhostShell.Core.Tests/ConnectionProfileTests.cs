using System.Text.Json;

namespace GhostShell.Core.Tests;

public sealed class ConnectionProfileTests
{
    [Fact]
    public void Ssh_profile_keeps_only_durable_configuration_and_secret_references()
    {
        var password = new SecretRef("vault-password-42");
        var profile = CreateSsh(new ConnectionAuthentication.Password(password));

        Assert.Equal(ConnectionKind.Ssh, profile.ConnectionKind);
        Assert.Equal(new DefinitionKey(DefinitionKind.Connection, "production"), profile.Key);
        Assert.Equal(password, Assert.IsType<ConnectionAuthentication.Password>(profile.Authentication).PasswordSecret);
        Assert.Equal("production", profile.Tags[0]);

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<ConnectionProfile>(json);

        Assert.NotNull(restored);
        Assert.Equal(profile.Id, restored.Id);
        Assert.Equal(ConnectionKind.Ssh, restored.ConnectionKind);
        Assert.Equal(password, Assert.IsType<ConnectionAuthentication.Password>(restored.Authentication).PasswordSecret);
    }

    [Fact]
    public void Non_ssh_profile_rejects_ssh_authentication()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfile(
            new ConnectionId("local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local shell",
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable));
    }

    [Fact]
    public void Ssh_profile_requires_an_explicit_host_key_policy()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfile(
            new ConnectionId("server"),
            ConnectionProfile.CurrentSchemaVersion,
            "Server",
            new ConnectionEndpoint.Ssh("server.example"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(15)),
            SshHostKeyPolicy.NotApplicable));
    }

    [Fact]
    public void Startup_environment_rejects_duplicate_names()
    {
        var variables = new[]
        {
            new ConnectionEnvironmentVariable("REGION", new ConnectionEnvironmentValue.PlainText("west")),
            new ConnectionEnvironmentVariable("REGION", new ConnectionEnvironmentValue.PlainText("east")),
        };

        Assert.Throws<ArgumentException>(() => new ConnectionStartup(environment: variables));
    }

    [Fact]
    public void Startup_command_is_trimmed_and_rejects_multiline_input()
    {
        Assert.Equal("npm run dev", new ConnectionStartup(command: "  npm run dev  ").Command);
        Assert.Null(new ConnectionStartup(command: "   ").Command);
        Assert.Throws<ArgumentException>(() => new ConnectionStartup(command: "ls\nrm -rf /"));
    }

    [Fact]
    public void Enabled_keepalive_requires_positive_timing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ConnectionKeepAlive.EnabledEvery(TimeSpan.Zero));
    }

    private static ConnectionProfile CreateSsh(ConnectionAuthentication authentication) =>
        new(
            new ConnectionId("production"),
            ConnectionProfile.CurrentSchemaVersion,
            "Production",
            new ConnectionEndpoint.Ssh("prod.example", username: "deploy"),
            authentication,
            new ConnectionStartup(
                "/srv/app",
                [new("REGION", new ConnectionEnvironmentValue.PlainText("us-east"))]),
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(30)),
            SshHostKeyPolicy.Strict,
            ["production"]);
}
