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
    public static void Main(string[] args)
    {
        // macOS will only host the UI on the process's first thread, and an
        // async Main leaves it at the first await that does real work —
        // resolving an encryption key from the keychain, say. So this thread
        // never awaits: it waits the asynchronous preparation out, starts
        // the lifetime exactly where the platform demands it, then waits the
        // finalization out the same way. There is no dispatcher before or
        // after the lifetime, so neither block can deadlock anything.
        var prepared = PrepareAsync(args).GetAwaiter().GetResult();
        if (prepared is null)
        {
            return;
        }

        var (services, instanceCoordinator) = prepared.Value;
        try
        {
            try
            {
                instanceCoordinator.RegisterActivationHandler(RequestMainWindowActivation);
                BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(
                    args,
                    Avalonia.Controls.ShutdownMode.OnMainWindowClose);
                instanceCoordinator.StopAcceptingActivations();
                FinalizeAsync(services).GetAwaiter().GetResult();
            }
            finally
            {
                services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            instanceCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Everything that must happen before the window can exist, on whatever
    /// threads it needs. Null means startup already ended — a helper run, a
    /// second instance, or a failure this method has already reported.
    /// </summary>
    private static async Task<(ServiceProvider Services, SingleInstanceCoordinator Coordinator)?>
        PrepareAsync(string[] args)
    {
        ConfigureDockDiagnostics();

        var helperExitCode = await ConnectionCredentialProcessHost.TryRunAsync(
            args,
            CancellationToken.None);
        if (helperExitCode is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return null;
        }

        var instanceStart = await SingleInstanceCoordinator.StartAsync(
            GhostShellDataPaths.CreateDefault().DataDirectory,
            CancellationToken.None);
        if (instanceStart is SingleInstanceStartResult.ExistingInstanceActivated)
        {
            return null;
        }
        if (instanceStart is SingleInstanceStartResult.Failure instanceFailure)
        {
            ReportStartupFailure(
                instanceFailure.Error.StableCode,
                instanceFailure.Error.Message,
                args);
            return null;
        }

        var instanceCoordinator =
            ((SingleInstanceStartResult.Primary)instanceStart).Coordinator;
        var services = DesktopComposition.CreateServiceProvider();
        try
        {
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
                Console.Error.WriteLine(
                    $"GhostSHELL cannot open this profile: {encryptionError}");
                Environment.ExitCode = 1;
                DesktopStartupFailurePresenter.TryShow(
                    "GhostSHELL cannot open this profile",
                    encryptionError,
                    args);
                return Abandon();
            }

            Task<string?> InitializeProfileAsync() => InitializeProfileCoreAsync(services);

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
                return Abandon();
            }

            return (services, instanceCoordinator);
        }
        catch
        {
            _ = Abandon();
            throw;
        }

        (ServiceProvider, SingleInstanceCoordinator)? Abandon()
        {
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            instanceCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }
    }

    private static async Task<string?> InitializeProfileCoreAsync(IServiceProvider services)
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
        startupState.MarkProfileInitialized();
        return null;
    }

    private static async Task FinalizeAsync(ServiceProvider services)
    {
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
