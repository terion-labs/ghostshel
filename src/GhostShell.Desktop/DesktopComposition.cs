using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.Agent.Providers;
using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Application.Previews;
using GhostShell.Browser;
using GhostShell.Databases;
using GhostShell.Previews;
using GhostShell.Files;
using GhostShell.Infrastructure;
using GhostShell.Mcp;
using GhostShell.Monitoring;
using GhostShell.SessionHost;
using GhostShell.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Desktop;

public static class DesktopComposition
{
    public static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostAccessibilityPreferencesSource>(_ =>
            HostAccessibilityPreferencesSourceSelector.CreateForCurrentPlatform());
        services.AddSingleton<IActiveWindowBoundsSource>(_ =>
            ActiveWindowBoundsSourceSelector.CreateForCurrentPlatform());
        services.AddSingleton(_ => SqliteStorageOptions.CreateDefault());
        services.AddSingleton(_ => LocalArtifactPaths.CreateDefault());
        services.AddSingleton<GhostShellDatabase>();
        services.AddSingleton<IAuditStore, SqliteAuditStore>();
        services.AddSingleton<IAgentRunAuditReader, SqliteAgentRunAuditReader>();
        services.AddSingleton<AgentAuditRecovery>();
        services.AddSingleton(BuiltInAgentTools.Catalog);
        services.AddSingleton<AgentCapabilityBroker>();
        services.AddSingleton<IAgentCapabilityBroker>(provider =>
            provider.GetRequiredService<AgentCapabilityBroker>());
        services.AddSingleton<IAgentAuthorizationConsumer>(provider =>
            provider.GetRequiredService<AgentCapabilityBroker>());
        services.AddSingleton<IAgentMcpRunAuthorityVerifier>(provider =>
            provider.GetRequiredService<AgentCapabilityBroker>());
        services.AddSingleton<AgentTerminalActionComposer>();
        services.AddSingleton<AgentBrowserActionComposer>();
        services.AddSingleton<AgentFileActionComposer>();
        services.AddSingleton<AgentProcessListActionComposer>();
        services.AddSingleton<AgentMcpToolCallActionComposer>();
        services.AddSingleton<AgentPanelActionComposer>();
        services.AddSingleton<AgentWorkspaceGraphActionComposer>();
        services.AddSingleton<TerminalStartupCommandDispatcher>();
        services.AddSingleton<ISecretAccessAuditSink, SqliteSecretAccessAuditSink>();
        services.AddSingleton(provider => PlatformSecretVaultFactory.Create(
            new SecretVaultFactoryOptions
            {
                DataDirectory = Path.GetDirectoryName(
                    provider.GetRequiredService<SqliteStorageOptions>().DatabasePath),
                AuditSink = provider.GetRequiredService<ISecretAccessAuditSink>(),
            }));
        services.AddSingleton(provider =>
            provider.GetRequiredService<SecretVaultFactoryResult>().Diagnostic);
        services.AddSingleton<ISecretVault>(provider =>
            provider.GetRequiredService<SecretVaultFactoryResult>().Vault);
        services.AddSingleton(new ConnectionCredentialBrokerOptions
        {
            SelfReentry = SelfReentryLaunch.Detect(typeof(DesktopComposition).Assembly.Location),
        });
        services.AddSingleton<IConnectionCredentialBroker, ConnectionCredentialBroker>();
        services.AddSingleton(_ => ConnectionRuntimeOptions.Detect());
        services.AddSingleton<IConnectionExecutableLocator, PathConnectionExecutableLocator>();
        services.AddSingleton<IConnectionCommandRunner, ProcessConnectionCommandRunner>();
        services.AddSingleton<IConnectionRuntimeAdapter, LocalConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntimeAdapter, SshConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntimeAdapter, DockerConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntimeAdapter, WslConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntime, ConnectionRuntime>();
        services.AddSingleton<IConnectionCommandExecutor, ConnectionCommandExecutor>();
        services.AddSingleton<SshKnownHostStore>();
        services.AddSingleton<ISshHostKeyTrustStore>(provider =>
            provider.GetRequiredService<SshKnownHostStore>());
        services.AddSingleton<IConnectionSecurityRuntime, ConnectionSecurityRuntime>();
        services.AddSingleton<SqliteFilePreviewPreferences>();
        services.AddSingleton<IFilePreviewPreferences>(provider =>
            provider.GetRequiredService<SqliteFilePreviewPreferences>());
        services.AddSingleton(provider =>
            new PreviewContentCache(provider.GetRequiredService<IFilePreviewPreferences>()));
        services.AddSingleton<IPreviewCacheControl>(provider =>
            provider.GetRequiredService<PreviewContentCache>());
        services.AddSingleton<IInMemoryDatabaseRegistry, SqliteInMemoryDatabaseRegistry>();
        services.AddSingleton(provider => new CatalogFileProviderRuntime(
            provider.GetRequiredService<IDefinitionCatalog>(),
            provider.GetRequiredService<ISecretVault>(),
            provider.GetRequiredService<ISshHostKeyTrustStore>(),
            provider.GetRequiredService<IConnectionSecurityRuntime>(),
            provider.GetRequiredService<IConnectionRuntime>(),
            provider.GetRequiredService<PreviewContentCache>()));
        services.AddSingleton<IFilePanelClient>(provider =>
            provider.GetRequiredService<CatalogFileProviderRuntime>());
        services.AddSingleton<IFileTransferQueueClient>(provider =>
            provider.GetRequiredService<CatalogFileProviderRuntime>());
        services.AddSingleton<IFilePanelSessionFactory, FilePanelSessionFactory>();
        services.AddSingleton<BrowserPanelSessionFactory>();
        services.AddSingleton<IBrowserPanelSessionFactory>(provider =>
            provider.GetRequiredService<BrowserPanelSessionFactory>());
        services.AddSingleton<ISystemMonitorPanelSessionFactory, SystemMonitorPanelSessionFactory>();
        services.AddSingleton<IDatabaseTunnelFactory, SshNetDatabaseTunnelFactory>();
        services.AddSingleton<IDatabasePanelClient, DatabasePanelClient>();
        services.AddSingleton<IImagePreviewDecoder, MagickImagePreviewDecoder>();
        services.AddSingleton<IArchiveTableOfContents, ArchiveTableOfContents>();
        // PDFium ships native binaries for the desktop platforms only, and the
        // preview treats a missing renderer as "this build cannot open PDFs"
        // rather than failing at the moment a user selects one.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            services.AddSingleton<IPdfPreviewRenderer, PdfiumPreviewRenderer>();
        }
        services.AddSingleton<IFileProviderProfileRuntime>(provider =>
            provider.GetRequiredService<CatalogFileProviderRuntime>());
        services.AddSingleton<CatalogAiProviderRuntime>();
        services.AddSingleton<IAiProviderProfileRuntime>(provider =>
            provider.GetRequiredService<CatalogAiProviderRuntime>());
        services.AddSingleton<IAgentApprovalPrincipal, DesktopAgentApprovalPrincipal>();
        services.AddSingleton<IAgentProviderResolver, CatalogAgentProviderResolver>();
        services.AddSingleton<AgentMcpSessionHost>();
        services.AddSingleton<IAgentMcpSessionHost>(provider =>
            provider.GetRequiredService<AgentMcpSessionHost>());
        services.AddSingleton<IMcpServerDiagnostics>(provider =>
            provider.GetRequiredService<AgentMcpSessionHost>());
        services.AddSingleton<IMcpCredentialSessionInvalidator>(provider =>
            provider.GetRequiredService<AgentMcpSessionHost>());
        services.AddSingleton<GovernedAgentRuntime>();
        services.AddSingleton<IGovernedAgentRuntime>(provider =>
            provider.GetRequiredService<GovernedAgentRuntime>());
        services.AddSingleton(typeof(IDefinitionRepository<>), typeof(SqliteDefinitionRepository<>));
        services.AddSingleton<ILayoutGraphStore, SqliteLayoutGraphStore>();
        services.AddSingleton<IDefinitionCatalog, DefinitionCatalog>();
        services.AddSingleton<IDefinitionBundleStore, SqliteDefinitionBundleStore>();
        services.AddSingleton<IApplicationRunStore, SqliteApplicationRunStore>();
        services.AddSingleton<SqliteRuntimeRecoveryStore>();
        services.AddSingleton<IRuntimeRecoveryStore>(provider =>
            provider.GetRequiredService<SqliteRuntimeRecoveryStore>());
        services.AddSingleton<IRuntimeRecoveryDataControl>(provider =>
            provider.GetRequiredService<SqliteRuntimeRecoveryStore>());
        services.AddSingleton<ILocalArtifactControl, FileSystemLocalArtifactControl>();
        services.AddSingleton<IOnboardingProgressStore, SqliteOnboardingProgressStore>();
        services.AddSingleton<ISessionRestorePreferenceStore,
            SqliteSessionRestorePreferenceStore>();
        services.AddSingleton<SqliteRecentSessionStore>();
        services.AddSingleton<IRecentSessionStore>(provider =>
            provider.GetRequiredService<SqliteRecentSessionStore>());
        services.AddSingleton<IRecentSessionRetentionStore>(provider =>
            provider.GetRequiredService<SqliteRecentSessionStore>());
        services.AddSingleton<IRecentSessionHistoryExporter,
            DeterministicRecentSessionHistoryExporter>();
        services.AddSingleton<IDiagnosticsBundleExporter, DeterministicDiagnosticsBundleExporter>();
        services.AddSingleton<IDiagnosticsBundleRequestSource, SafeDiagnosticsBundleRequestSource>();
        services.AddSingleton<IDiagnosticsArtifactPresenter, DesktopDiagnosticsArtifactPresenter>();
        services.AddSingleton<RecentSessionHistory>();
        services.AddSingleton<RuntimeRecoveryWriter>();
        services.AddSingleton<DesktopRunFinalizer>();
        services.AddSingleton<IRecoveryCoordinator, RecoveryCoordinator>();
        services.AddSingleton<SessionRestoreCoordinator>();
        services.AddSingleton<ApplicationStartupState>();
        services.AddSingleton<ITerminalSessionFactory>(_ =>
            TerminalSessionFactorySelector.CreateForCurrentPlatform());
        services.AddSingleton<ISessionLifecyclePolicy, DesktopLifecyclePolicy>();
        services.AddSingleton<InMemorySessionHostClient>();
        services.AddSingleton<ISessionHostClient>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentTerminalSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentBrowserSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentFileSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentProcessSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentPanelSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentWorkspaceGraphSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IGlobalHotkeyService>(_ =>
            GlobalHotkeyServiceSelector.CreateForCurrentPlatform());
        services.AddSingleton<IScreenColorSampler>(_ =>
            ScreenColorSamplerSelector.Create());
        services.AddSingleton(provider => new OnboardingViewModel(
            provider.GetRequiredService<IOnboardingProgressStore>(),
            provider.GetRequiredService<IDefinitionCatalog>(),
            provider.GetRequiredService<IConnectionRuntime>(),
            provider.GetRequiredService<ISecretVault>().Availability));
        services.AddSingleton<RecoveryDataControlViewModel>();
        services.AddSingleton<LocalArtifactControlViewModel>();
        services.AddSingleton<IProductComponentCatalog, DesktopProductComponentCatalog>();
        services.AddSingleton<IBrowserRendererViewFactory, DesktopBrowserRendererViewFactory>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<GhostShell.App.QuickTerminalController>();
        services.AddSingleton<GhostShell.App.App>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
