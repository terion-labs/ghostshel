using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GhostShell.App;
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
        var runStore = services.GetRequiredService<IApplicationRunStore>();
        var startResult = await runStore.BeginRunAsync(CancellationToken.None);
        if (!startResult.IsSuccess)
        {
            ReportLifecycleFailure(
                "initialize its recovery marker",
                startResult.Error!);
            DesktopStartupFailurePresenter.TryShow(
                "GhostSHELL could not open this profile",
                $"Local application data is unavailable ({startResult.Error!.Code}).",
                args);
            return;
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
            Environment.ExitCode = 1;
            DesktopStartupFailurePresenter.TryShow(
                "GhostSHELL could not verify agent history",
                "The local agent audit trail is unavailable or invalid.",
                args);
            return;
        }

        var catalog = services.GetRequiredService<IDefinitionCatalog>();
        var catalogResult = await catalog.InitializeAsync(CancellationToken.None);
        if (!catalogResult.IsSuccess)
        {
            Console.Error.WriteLine(
                $"GhostSHELL could not load its durable definitions "
                + $"({catalogResult.Error!.Code}).");
            Environment.ExitCode = 1;
            DesktopStartupFailurePresenter.TryShow(
                "GhostSHELL could not load this profile",
                $"Saved connections and workspaces are unavailable "
                + $"({catalogResult.Error.Code}).",
                args);
            return;
        }

        instanceCoordinator.RegisterActivationHandler(RequestMainWindowActivation);
        BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(
            args,
            Avalonia.Controls.ShutdownMode.OnMainWindowClose);
        instanceCoordinator.StopAcceptingActivations();

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
                startResult.Value!.RunId,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!completion.IsSuccess)
        {
            ReportLifecycleFailure(
                "finalize its recovery state",
                completion.Error!);
        }
    }

    internal static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder
            .Configure(() => services.GetRequiredService<GhostShellApplication>())
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ReportLifecycleFailure(
        string operation,
        ApplicationRunError error)
    {
        Console.Error.WriteLine($"GhostSHELL could not {operation} ({error.Code}).");
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
