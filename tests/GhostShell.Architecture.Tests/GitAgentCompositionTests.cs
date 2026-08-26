using GhostShell.Application;
using GhostShell.Desktop;
using GhostShell.SessionHost;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Architecture.Tests;

public sealed class GitAgentCompositionTests
{
    [Fact]
    public async Task DesktopCompositionUsesOneHostedGitBoundaryAndCoordinator()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var sessionClient = services.GetRequiredService<ISessionHostClient>();
        var gitHost = services.GetRequiredService<IAgentGitSessionHost>();
        var concrete = services.GetRequiredService<InMemorySessionHostClient>();
        var firstCoordinator = services
            .GetRequiredService<IGitRepositoryMutationCoordinator>();
        var repeatedCoordinator = services
            .GetRequiredService<IGitRepositoryMutationCoordinator>();

        Assert.Same(concrete, sessionClient);
        Assert.Same(concrete, gitHost);
        Assert.Same(firstCoordinator, repeatedCoordinator);
        Assert.NotNull(services.GetRequiredService<IGitPanelSessionFactory>());
        Assert.NotNull(services.GetRequiredService<AgentGitActionComposer>());
    }
}
