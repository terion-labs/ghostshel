using Avalonia.Threading;

namespace GhostShell.App;

public interface IUiThreadDispatcher
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

internal sealed class AvaloniaUiThreadDispatcher : IUiThreadDispatcher
{
    public static AvaloniaUiThreadDispatcher Instance { get; } = new();

    private AvaloniaUiThreadDispatcher()
    {
    }

    public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (Avalonia.Application.Current is null
            || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
    }
}
