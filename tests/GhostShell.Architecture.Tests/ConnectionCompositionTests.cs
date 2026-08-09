using GhostShell.Application;
using GhostShell.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Architecture.Tests;

public sealed class ConnectionCompositionTests
{
    [Fact]
    public async Task Desktop_composes_one_runtime_with_all_connection_adapters()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var runtime = services.GetRequiredService<IConnectionRuntime>();
        var securityRuntime = services.GetRequiredService<IConnectionSecurityRuntime>();
        var sqlLanguage = services.GetRequiredService<ISqlLanguageService>();
        var adapters = services.GetServices<IConnectionRuntimeAdapter>().ToArray();

        Assert.Same(runtime, services.GetRequiredService<IConnectionRuntime>());
        Assert.Same(
            securityRuntime,
            services.GetRequiredService<IConnectionSecurityRuntime>());
        Assert.Same(
            sqlLanguage,
            services.GetRequiredService<ISqlLanguageService>());
        Assert.Equal(4, adapters.Length);
        Assert.Equal(
            [
                GhostShell.Core.ConnectionKind.Local,
                GhostShell.Core.ConnectionKind.Ssh,
                GhostShell.Core.ConnectionKind.Docker,
                GhostShell.Core.ConnectionKind.Wsl,
            ],
            adapters.Select(adapter => adapter.Kind).OrderBy(kind => kind));
    }
}
