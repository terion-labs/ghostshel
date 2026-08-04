using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dock.Settings;
using GhostShell.App;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Infrastructure;
using GhostShell.SessionHost;
using Microsoft.Extensions.DependencyInjection;
using GhostShellApplication = GhostShell.App.App;

namespace GhostShell.Desktop;

internal static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        ConfigureDockDiagnostics();

        var helperExitCode = await ConnectionCredentialProcessHost.TryRunAsync(
            args,
            CancellationToken.None);
        if (helperExitCode is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return;
        }

        var instanceStart = await SingleInstanceCoordinator.StartAsync(
            GhostShellDataPaths.CreateDefault().DataDirectory,
            CancellationToken.None);
        if (instanceStart is SingleInstanceStartResult.ExistingInstanceActivated)
        {
            return;
        }
        if (instanceStart is SingleInstanceStartResult.Failure instanceFailure)
        {
            ReportStartupFailure(
                instanceFailure.Error.StableCode,
                instanceFailure.Error.Message,
                args);
            return;
        }

        var instanceCoordinator =
            ((SingleInstanceStartResult.Primary)instanceStart).Coordinator;
        // These scopes are disposed after Avalonia stops pumping its synchronization context.
        // Configured disposal keeps shutdown continuations off the stopped UI dispatcher.
        await using var instanceCoordinatorLifetime =
            instanceCoordinator.ConfigureAwait(false);

        var services = DesktopComposition.CreateServiceProvider();
        await using var serviceProviderLifetime = services.ConfigureAwait(false);

        // Before anything opens the configuration database: an encrypted
        // database needs its key in hand for the very first connection —
        // from the OS keystore, or, when protection sealed the keys under
        // the PIN, from the unlock that has not happened yet.
        var protection = services.GetRequiredService<IStartupProtection>()
            as StartupProtectionRuntime;
        var encryption = services.GetRequiredService<ApplicationEncryptionRuntime>();
        await encryption.InitializeAsync(
            wrappedKeysPending: protection?.HoldsWrappedKeys ?? false,
            CancellationToken.None);
        if (encryption.StartupError is { } encryptionError)
        {
            Console.Error.WriteLine($"GhostSHELL cannot open this profile: {encryptionError}");
            Environment.ExitCode = 1;
            DesktopStartupFailurePresenter.TryShow(
                "GhostSHELL cannot open this profile",
                encryptionError,
                args);
            return;
        }

        async Task<string?> InitializeProfileAsync()
        {
            var runStore = services.GetRequiredService<IApplicationRunStore>();
            var startResult = await runStore.BeginRunAsync(CancellationToken.None);
            if (!startResult.IsSuccess)
            {
                ReportLifecycleFailure(
                    "initialize its recovery marker",
                    startResult.Error!);
                return $"Local application data is unavailable ({startResult.Error!.Code}).";
            }

            var startupState = services.GetRequiredService<ApplicationStartupState>();
            startupState.Initialize(startResult.Value!);

            var agentAuditRecovery = await services
                .GetRequiredService<AgentAuditRecovery>()
                .RecoverAsync(CancellationToken.None);
            if (!agentAuditRecovery.IsSuccess)
            {
                Console.Error.WriteLine(
                    $"GhostSHELL could not reconcile its agent audit trail "
                    + $"({agentAuditRecovery.Error!.Code}).");
                return "The local agent audit trail is unavailable or invalid.";
            }

            var catalog = services.GetRequiredService<IDefinitionCatalog>();
            var catalogResult = await catalog.InitializeAsync(CancellationToken.None);
            if (!catalogResult.IsSuccess)
            {
                Console.Error.WriteLine(
                    $"GhostSHELL could not load its durable definitions "
                    + $"({catalogResult.Error!.Code}).");
                return $"Saved connections and workspaces are unavailable "
                    + $"({catalogResult.Error.Code}).";
            }

            // Failure-tolerant by design: unreadable settings mean the
            // defaults, never a startup error.
            await services.GetRequiredService<SqliteFilePreviewPreferences>()
                .InitializeAsync(CancellationToken.None);
            return null;
        }

        if (encryption.AwaitingUnlock)
        {
            // The keys arrive with the PIN; everything that needs the
            // database runs then, behind the lock screen the window opens
            // with. A failure at that point is fatal exactly as it would
            // have been here.
            DeferredStartupCoordinator.Arm(
                services.GetRequiredService<IStartupProtection>(),
                InitializeProfileAsync);
        }
        else if (await InitializeProfileAsync() is { } profileError)
        {
            Environment.ExitCode = 1;
            DesktopStartupFailurePresenter.TryShow(
                "GhostSHELL could not open this profile",
                profileError,
                args);
            return;
        }

        instanceCoordinator.RegisterActivationHandler(RequestMainWindowActivation);
        BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(
            args,
            Avalonia.Controls.ShutdownMode.OnMainWindowClose);
        instanceCoordinator.StopAcceptingActivations();

        // The run began either before the lifetime or, with sealed keys,
        // behind the lock screen; quitting at the lock screen means no run
        // marker was ever written and there is nothing to finalize.
        if (services.GetRequiredService<ApplicationStartupState>().Run is not { } run)
        {
            return;
        }

        var mainWindowViewModel = services.GetRequiredService<MainWindowViewModel>();
        // The desktop dispatcher no longer pumps once the classic lifetime returns.
        var completion = await services.GetRequiredService<DesktopRunFinalizer>()
            .FinalizeAsync(
                cancellationToken => QuiescePresentationAsync(
                    services.GetRequiredService<QuickTerminalController>(),
                    mainWindowViewModel,
                    cancellationToken),
                mainWindowViewModel.FlushRecentSessionHistoryAsync,
                _ => services.GetRequiredService<InMemorySessionHostClient>().DisposeAsync(),
                run.RunId,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!completion.IsSuccess)
        {
            ReportLifecycleFailure(
                "finalize its recovery state",
                completion.Error!);
        }
    }

    private static void ConfigureDockDiagnostics()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GHOSTSHELL_DOCK_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        DockSettings.EnableDiagnosticsLogging = true;
        DockSettings.DiagnosticsLogHandler = Console.Error.WriteLine;
    }

    internal static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder
            .Configure(() => services.GetRequiredService<GhostShellApplication>())
            .UsePlatformDetect()
            .WithInterFont()
            .ConfigureFonts(fontManager =>
                fontManager.AddFontCollection(new GhostShellTerminalFontCollection()))
            .SetDragPreviewOpacity(0.9)
            .LogToTrace();

    private static void ReportLifecycleFailure(
        string operation,
        ApplicationRunError error)
    {
        Console.Error.WriteLine(
            $"GhostSHELL could not {operation} ({error.Code}): {error.Message}");
        Environment.ExitCode = 1;
    }

    private static void ReportStartupFailure(
        string stableCode,
        string message,
        string[] args)
    {
        Console.Error.WriteLine($"GhostSHELL could not start ({stableCode}).");
        Environment.ExitCode = 1;
        DesktopStartupFailurePresenter.TryShow(
            "GhostSHELL could not start",
            message,
            args);
    }

    private static void RequestMainWindowActivation()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime
                    {
                        MainWindow: { } mainWindow,
                    })
            {
                return;
            }

            if (mainWindow.WindowState == WindowState.Minimized)
            {
                mainWindow.WindowState = WindowState.Normal;
            }

            if (!mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            mainWindow.Activate();
        });
    }

    private static async Task QuiescePresentationAsync(
        QuickTerminalController quickTerminalController,
        MainWindowViewModel mainWindowViewModel,
        CancellationToken cancellationToken)
    {
        quickTerminalController.Dispose();
        await mainWindowViewModel.QuiesceForShutdownAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
