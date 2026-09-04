using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GhostShell.App.Views;
using GhostShell.Application;

namespace GhostShell.App;

/// <summary>
/// Shows a session-only password dialog in the active GhostSHELL window. The
/// returned material crosses into the network runtime only and is never persisted.
/// </summary>
public sealed class AvaloniaNetworkPasswordPrompt : INetworkPasswordPrompt
{
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    public async ValueTask<NetworkConnectionResult<SecretMaterial>> RequestPasswordAsync(
        NetworkPasswordPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            return await InvokeOnUiThreadAsync(
                    () => ShowPromptAsync(request, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NetworkConnectionResult<SecretMaterial>.Fail(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.AuthenticationRequired,
                    "network_password_prompt_failed",
                    "GhostSHELL could not open the password prompt.",
                    retryable: true));
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private static async Task<NetworkConnectionResult<SecretMaterial>> ShowPromptAsync(
        NetworkPasswordPromptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop
            || ActiveWindow(desktop) is not { } owner)
        {
            return NetworkConnectionResult<SecretMaterial>.Fail(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.AuthenticationRequired,
                    "network_password_window_unavailable",
                    $"{request.ConnectionName} needs a password, but no GhostSHELL window can show the prompt.",
                    retryable: true));
        }

        var dialog = new DatabasePasswordPromptDialog(request.ConnectionName);
        using var cancellationRegistration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (dialog.IsVisible)
                {
                    dialog.Close(null);
                }
            }));
        var result = await dialog.ShowDialog<DatabasePasswordPromptResult?>(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null)
        {
            return Cancelled();
        }

        if (string.IsNullOrEmpty(result.Password))
        {
            return NetworkConnectionResult<SecretMaterial>.Fail(
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.AuthenticationRequired,
                    "network_password_empty",
                    $"{request.ConnectionName} needs a non-empty password.",
                    retryable: true));
        }

        return NetworkConnectionResult<SecretMaterial>.Succeed(
            SecretMaterial.TakeOwnership(Encoding.UTF8.GetBytes(result.Password)));
    }

    private static Window? ActiveWindow(IClassicDesktopStyleApplicationLifetime desktop) =>
        desktop.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsActive)
        ?? desktop.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsVisible)
        ?? desktop.MainWindow;

    private static async Task<T> InvokeOnUiThreadAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await action();
        }

        var scheduled = await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
        return await scheduled.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NetworkConnectionResult<SecretMaterial> Cancelled() =>
        NetworkConnectionResult<SecretMaterial>.Fail(
            new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "network_password_prompt_cancelled",
                "The password prompt was cancelled.",
                retryable: true));
}
