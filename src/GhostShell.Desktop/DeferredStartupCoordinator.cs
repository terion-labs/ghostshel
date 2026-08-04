using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GhostShell.Application;

namespace GhostShell.Desktop;

/// <summary>
/// Runs the database-dependent part of startup after the unlock, when the
/// encryption keys arrived sealed under the PIN. The window opens locked;
/// the first successful unlock hands the keys to the encryption runtime and
/// this coordinator then does what startup would have done — once, off the
/// UI thread. A failure at that point is exactly as fatal as it would have
/// been before the window existed, and is reported the same way before the
/// application closes.
/// </summary>
internal static class DeferredStartupCoordinator
{
    public static void Arm(
        IStartupProtection protection,
        Func<Task<string?>> initializeProfile)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(initializeProfile);
        var armed = 0;
        protection.Changed += OnChanged;

        async void OnChanged(object? sender, EventArgs e)
        {
            if (protection.IsLocked
                || Interlocked.Exchange(ref armed, 1) != 0)
            {
                return;
            }

            protection.Changed -= OnChanged;
            string? error;
            try
            {
                error = await Task.Run(initializeProfile);
            }
            catch (Exception exception)
            {
                error = $"Deferred startup failed unexpectedly: {exception.Message}";
            }

            if (error is null)
            {
                return;
            }

            Console.Error.WriteLine($"GhostSHELL could not open this profile: {error}");
            Environment.ExitCode = 1;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown(1);
                }
            });
        }
    }
}
