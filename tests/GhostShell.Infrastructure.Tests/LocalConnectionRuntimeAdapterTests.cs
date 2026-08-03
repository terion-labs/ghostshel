using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class LocalConnectionRuntimeAdapterTests
{
    [Fact]
    public async Task Plan_uses_a_resolved_shell_and_keeps_secret_environment_out_of_launch_data()
    {
        var secret = new SecretRef("secret-local-env");
        using var vault = new RecordingSecretVault();
        vault.Add(secret, "local-test");
        var locator = new RecordingExecutableLocator();
        locator.Add("/bin/zsh", "/resolved/bin/zsh");
        var runner = new RecordingCommandRunner();
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            runner,
            new ConnectionRuntimeOptions(ConnectionHostPlatform.MacOs, "/bin/zsh"));
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Local(),
            startup: new ConnectionStartup(
                "/work tree",
                [
                    new ConnectionEnvironmentVariable(
                        "PLAIN_VALUE",
                        new ConnectionEnvironmentValue.PlainText("amber value")),
                    new ConnectionEnvironmentVariable(
                        "SECRET_VALUE",
                        new ConnectionEnvironmentValue.Secret(secret)),
                ],
                command: "npm run dev"),
            id: "local-test");
        var progress = new RecordingConnectionProgress();

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            progress,
            CancellationToken.None));

        Assert.Equal("/resolved/bin/zsh", plan.Launch.Executable);
        Assert.Equal("/work tree", plan.Launch.WorkingDirectory);
        Assert.Equal(new ConnectionId("local-test"), plan.Launch.ConnectionId);
        Assert.Equal(
            "Local: Test connection",
            plan.Launch.ConnectionMetadata?.ConnectionBoundary);
        Assert.Equal(
            "/work tree",
            plan.Launch.ConnectionMetadata?.InitialWorkingDirectory);
        Assert.Empty(plan.Launch.Arguments);
        Assert.Equal("npm run dev", plan.Launch.InitialCommand);
        Assert.Equal("amber value", Assert.Single(plan.Launch.Environment).Value);
        Assert.DoesNotContain("SECRET_VALUE", plan.Launch.Environment.Keys);
        var requirement = Assert.Single(plan.SecretRequirements);
        Assert.Equal(ConnectionSecretRole.EnvironmentVariable, requirement.Role);
        Assert.Equal(secret, requirement.Reference);
        Assert.Equal("SECRET_VALUE", requirement.EnvironmentVariableName);
        Assert.Contains(ConnectionPlanWarning.SecretBrokerRequired, plan.Warnings);
        Assert.Null(vault.LastMaterial);
        Assert.Empty(vault.ResolveRequests);

        var request = Assert.Single(vault.MetadataRequests);
        Assert.Equal(new SecretScope(SecretScopeKind.Connection, "local-test"), request.Scope);
        Assert.Equal(
            new SecretUsePurpose(SecretUseKind.ConnectionEnvironment, "local-test"),
            request.Purpose);
        Assert.Equal(
            [
                ConnectionProgressStage.ValidatingProfile,
                ConnectionProgressStage.DetectingRuntime,
                ConnectionProgressStage.ResolvingCredentials,
                ConnectionProgressStage.BuildingLaunchPlan,
                ConnectionProgressStage.Completed,
            ],
            progress.Updates.Select(update => update.Stage));
        Assert.DoesNotContain(
            progress.Updates,
            update => update.Message.Contains("do-not-leak", StringComparison.Ordinal));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Explicit_shell_is_resolved_without_invoking_a_shell_command()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("custom shell", "/tools/custom shell");
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(ConnectionHostPlatform.Linux, "/bin/sh"));
        var profile = ConnectionRuntimeTestSupport.Profile(
            new ConnectionEndpoint.Local("custom shell"));

        var plan = ConnectionRuntimeTestSupport.Success(await adapter.PlanOpenAsync(
            profile,
            null,
            CancellationToken.None));

        Assert.Equal(["custom shell"], locator.Requests);
        Assert.Equal("/tools/custom shell", plan.Launch.Executable);
        Assert.Empty(plan.Launch.Arguments);
    }

    [Fact]
    public async Task Missing_shell_returns_a_typed_install_runtime_error()
    {
        using var vault = new RecordingSecretVault();
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            new RecordingExecutableLocator(),
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(ConnectionHostPlatform.Linux, "/missing/sh"));

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local()),
            null,
            CancellationToken.None));

        Assert.Equal(ConnectionRuntimeErrorCode.RuntimeMissing, error.Code);
        Assert.Equal(ConnectionRecoveryAction.InstallRuntime, error.RecoveryAction);
    }

    [Fact]
    public async Task Cancelled_plan_returns_a_typed_result_before_runtime_or_vault_access()
    {
        using var vault = new RecordingSecretVault();
        var locator = new RecordingExecutableLocator();
        locator.Add("/bin/sh", "/bin/sh");
        var adapter = new LocalConnectionRuntimeAdapter(
            vault,
            locator,
            new RecordingCommandRunner(),
            new ConnectionRuntimeOptions(ConnectionHostPlatform.Linux, "/bin/sh"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = ConnectionRuntimeTestSupport.Failure(await adapter.PlanOpenAsync(
            ConnectionRuntimeTestSupport.Profile(new ConnectionEndpoint.Local()),
            null,
            cancellation.Token));

        Assert.Equal(ConnectionRuntimeErrorCode.Cancelled, error.Code);
        Assert.Empty(locator.Requests);
        Assert.Empty(vault.ResolveRequests);
        Assert.Empty(vault.MetadataRequests);
    }
}
