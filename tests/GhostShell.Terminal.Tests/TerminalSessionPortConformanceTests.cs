using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal.Tests;

public sealed class TerminalSessionPortConformanceTests
{
    [Theory]
    [InlineData("GhostShell.Terminal.GhosttyTerminalSession")]
    [InlineData("GhostShell.Terminal.PortableTerminalSession")]
    public void Built_in_terminal_engines_implement_every_application_port(string typeName)
    {
        var implementation = typeof(TerminalSessionFactorySelector).Assembly
            .GetType(typeName, throwOnError: true);

        Assert.NotNull(implementation);
        Assert.True(typeof(ITerminalPanelSession).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalProcess).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalState).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalRendererAttachment).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalAutomation).IsAssignableFrom(implementation));
    }

    [Fact]
    public async Task Factory_preserves_process_environment_and_connection_identity()
    {
        var connectionId = new ConnectionId("terminal-port-test");
        var launch = new TerminalLaunchRequest(
            "/tmp",
            "/bin/sh",
            environment: new Dictionary<string, string> { ["LANG"] = "C" },
            connectionId: connectionId);
        var factory = new GhosttyTerminalSessionFactory();

        await using var session = await factory.CreateAsync(
            SessionId.New(),
            launch,
            CancellationToken.None);

        var process = Assert.IsAssignableFrom<ITerminalProcess>(session);
        Assert.Same(launch, process.Launch);
        Assert.Equal(connectionId, process.Launch.ConnectionId);
        Assert.Equal("C", process.Launch.Environment["LANG"]);
    }
}
