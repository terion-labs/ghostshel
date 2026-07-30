using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class ConnectionCommandExecutorTests
{
    [Fact]
    public void SshCommandsReuseAnAuthenticatedControlConnection()
    {
        var profile = SshProfile();
        var request = new ConnectionCommand(
            profile,
            "ps",
            ["-A"],
            TimeSpan.FromSeconds(5),
            1024);
        var arguments = ConnectionCommandExecutor.SshArguments(
            ["-p", "22", "-tt", "--", "host.example"],
            request,
            "/tmp/ghostshell-test-%C");

        Assert.DoesNotContain("-tt", arguments);
        AssertOption(arguments, "ControlMaster=auto");
        AssertOption(arguments, "ControlPersist=15");
        AssertOption(arguments, "ControlPath=/tmp/ghostshell-test-%C");
        Assert.Equal("--", arguments[^3]);
        Assert.Equal("host.example", arguments[^2]);
        Assert.Equal("'ps' '-A'", arguments[^1]);
    }

    [Fact]
    public void ControlConnectionIdentityChangesWithAuthenticationConfiguration()
    {
        var systemConfiguration = SshProfile();
        var sshAgent = new ConnectionProfile(
            systemConfiguration.Id,
            systemConfiguration.SchemaVersion,
            systemConfiguration.Name,
            systemConfiguration.Endpoint,
            new ConnectionAuthentication.SshAgent(),
            systemConfiguration.Startup,
            systemConfiguration.KeepAlive,
            systemConfiguration.HostKeyPolicy);

        var first = ConnectionCommandExecutor.SshControlPath(systemConfiguration);
        var repeated = ConnectionCommandExecutor.SshControlPath(systemConfiguration);
        var changed = ConnectionCommandExecutor.SshControlPath(sshAgent);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert.NotNull(first);
            Assert.Equal(first, repeated);
            Assert.NotEqual(first, changed);
            Assert.EndsWith("-%C", first, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(first);
            Assert.Null(repeated);
            Assert.Null(changed);
        }
    }

    private static ConnectionProfile SshProfile() =>
        ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
            new ConnectionAuthentication.None(),
            hostKeyPolicy: SshHostKeyPolicy.Strict);

    private static void AssertOption(
        IReadOnlyList<string> arguments,
        string expectedValue)
    {
        var valueIndex = -1;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (arguments[index - 1] == "-o" && arguments[index] == expectedValue)
            {
                valueIndex = index;
                break;
            }
        }

        Assert.True(valueIndex >= 0, $"Missing SSH option: {expectedValue}");
        Assert.True(valueIndex < arguments.IndexOf("--"));
    }
}
