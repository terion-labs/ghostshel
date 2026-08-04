using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.Agent.Providers;
using GhostShell.Application;
using GhostShell.Browser;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Infrastructure;
using GhostShell.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Architecture.Tests;

public sealed class CompositionTests
{
    /// <summary>
    /// The file preview opens databases by path, and the path comes from the
    /// composed file client rather than the one the Files tests construct
    /// directly. A wrapper that forgets to forward materialization compiles
    /// perfectly and fails only at the moment a user selects a database, so the
    /// composed chain is asserted here.
    /// </summary>
    [Fact]
    public async Task ComposedFileClientCanServeWholeFileContent()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var filePanel = services.GetRequiredService<IFilePanelClient>();

        Assert.IsAssignableFrom<IFileContentSource>(filePanel);
    }

    [Fact]
    public async Task DesktopGraphResolvesOneSessionHostClientAndPresentationRoot()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var firstClient = services.GetRequiredService<ISessionHostClient>();
        var secondClient = services.GetRequiredService<ISessionHostClient>();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var application = services.GetRequiredService<GhostShell.App.App>();
        var definitions = services.GetRequiredService<IDefinitionBundleStore>();
        var runStore = services.GetRequiredService<IApplicationRunStore>();
        var recentSessions = services.GetRequiredService<IRecentSessionStore>();
        var recentSessionRetention = services.GetRequiredService<IRecentSessionRetentionStore>();
        var historyExporter = services.GetRequiredService<IRecentSessionHistoryExporter>();
        var vault = services.GetRequiredService<ISecretVault>();
        var auditSink = services.GetRequiredService<ISecretAccessAuditSink>();
        var vaultDiagnostic = services.GetRequiredService<SecretVaultFactoryDiagnostic>();
        var filePanel = services.GetRequiredService<IFilePanelClient>();
        var transferQueue = services.GetRequiredService<IFileTransferQueueClient>();
        var fileProfiles = services.GetRequiredService<IFileProviderProfileRuntime>();
        var browserSessionFactory =
            services.GetRequiredService<IBrowserPanelSessionFactory>();
        var browserViewFactory =
            services.GetRequiredService<IBrowserRendererViewFactory>();
        var aiProfiles = services.GetRequiredService<IAiProviderProfileRuntime>();
        var concreteAiProfiles = services.GetRequiredService<CatalogAiProviderRuntime>();
        var governedAgent = services.GetRequiredService<IGovernedAgentRuntime>();
        var approvalPrincipal =
            services.GetRequiredService<IAgentApprovalPrincipal>();
        var capabilityBroker = services.GetRequiredService<IAgentCapabilityBroker>();
        var authorizationConsumer =
            services.GetRequiredService<IAgentAuthorizationConsumer>();
        var mcpRunAuthorityVerifier =
            services.GetRequiredService<IAgentMcpRunAuthorityVerifier>();
        var mcpSessionHost =
            services.GetRequiredService<IAgentMcpSessionHost>();
        var mcpCredentialSessionInvalidator =
            services.GetRequiredService<IMcpCredentialSessionInvalidator>();
        var agentTerminalHost =
            services.GetRequiredService<IAgentTerminalSessionHost>();
        var agentBrowserHost =
            services.GetRequiredService<IAgentBrowserSessionHost>();
        var agentProcessHost =
            services.GetRequiredService<IAgentProcessSessionHost>();
        var processActionComposer =
            services.GetRequiredService<AgentProcessListActionComposer>();
        var diagnosticsExporter = services.GetRequiredService<IDiagnosticsBundleExporter>();
        var diagnosticsSource = services.GetRequiredService<IDiagnosticsBundleRequestSource>();
        var diagnosticsPresenter = services.GetRequiredService<IDiagnosticsArtifactPresenter>();
        var recoveryStore = services.GetRequiredService<IRuntimeRecoveryStore>();
        var recoveryDataControl = services.GetRequiredService<IRuntimeRecoveryDataControl>();
        var recoveryDataViewModel = services.GetRequiredService<RecoveryDataControlViewModel>();
        var localArtifactControl = services.GetRequiredService<ILocalArtifactControl>();
        var localArtifactViewModel = services.GetRequiredService<LocalArtifactControlViewModel>();

        Assert.Same(firstClient, secondClient);
        Assert.Same(firstClient, viewModel.SessionClient);
        Assert.True(viewModel.IsLauncherVisible);
        Assert.NotNull(application);
        Assert.NotNull(definitions);
        Assert.NotNull(runStore);
        Assert.IsType<SqliteRecentSessionStore>(recentSessions);
        Assert.Same(recentSessions, recentSessionRetention);
        Assert.IsType<DeterministicRecentSessionHistoryExporter>(historyExporter);
        Assert.IsType<SqliteSecretAccessAuditSink>(auditSink);
        Assert.IsType<AuditedSecretVault>(vault);
        Assert.Same(vault.Availability, vaultDiagnostic.Availability);
        Assert.Same(filePanel, transferQueue);
        Assert.Same(filePanel, fileProfiles);
        Assert.Contains(filePanel.Profiles, profile => profile.Id == "builtin.files.home");
        Assert.Equal(
            "GhostShell.Browser.BrowserPanelSessionFactory",
            browserSessionFactory.GetType().FullName);
        Assert.Equal(
            BrowserCapabilityProfile.Production.Capabilities.Values,
            browserSessionFactory.Capabilities.Values);
        using (var browserView = browserViewFactory.Create())
        {
            Assert.Same(
                browserSessionFactory.Capabilities,
                browserView.Renderer.Capabilities);
        }

        var hostHello = Assert.IsType<HostResult<HostHello>.Success>(
            await firstClient.NegotiateAsync(
                new ClientHello(
                    [ProtocolVersions.Current],
                    BrowserCapabilityProfile.FullAutomationCandidate.Capabilities),
                OperationContext.ForHuman(
                    new ClientId("desktop-capability-check")),
                CancellationToken.None));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserReadState));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserNavigate));
        Assert.False(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserSnapshot));
        Assert.False(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserClick));
        Assert.False(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserFill));
        Assert.False(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserCheck));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserOriginGuard));
        Assert.Same(concreteAiProfiles, aiProfiles);
        Assert.Null(services.GetService<IAgentChatRuntime>());
        Assert.NotNull(governedAgent);
        Assert.Equal(
            approvalPrincipal.Actor.ClientId,
            viewModel.ClientId);
        Assert.IsType<AgentCapabilityBroker>(capabilityBroker);
        Assert.Same(capabilityBroker, authorizationConsumer);
        Assert.Same(capabilityBroker, mcpRunAuthorityVerifier);
        Assert.Same(
            mcpSessionHost,
            mcpCredentialSessionInvalidator);
        Assert.Same(firstClient, agentTerminalHost);
        Assert.Same(firstClient, agentBrowserHost);
        Assert.Same(firstClient, agentProcessHost);
        Assert.NotNull(processActionComposer);
        Assert.IsType<DeterministicDiagnosticsBundleExporter>(diagnosticsExporter);
        Assert.IsType<SafeDiagnosticsBundleRequestSource>(diagnosticsSource);
        Assert.IsType<DesktopDiagnosticsArtifactPresenter>(diagnosticsPresenter);
        Assert.Same(recoveryStore, recoveryDataControl);
        Assert.NotNull(recoveryDataViewModel);
        Assert.IsType<FileSystemLocalArtifactControl>(localArtifactControl);
        Assert.NotNull(localArtifactViewModel);
    }
}
