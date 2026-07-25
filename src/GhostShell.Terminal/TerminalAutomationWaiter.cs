using System.Runtime.ExceptionServices;
using GhostShell.Application;

namespace GhostShell.Terminal;

internal static class TerminalAutomationWaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    public static ValueTask<TerminalWaitOutcome> WaitForTextAsync(
        TerminalWaitForTextInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PollAsync(
            input.Timeout,
            initialContentRevision: null,
            readScreen,
            readSession,
            (snapshot, initialRevision, _) =>
                snapshot.PlainText.Contains(input.Text, StringComparison.Ordinal)
                    ? TerminalWaitOutcome.Matched(snapshot, initialRevision)
                    : null,
            cancellationToken,
            timeProvider ?? TimeProvider.System);
    }

    public static ValueTask<TerminalWaitOutcome> WaitForChangeAsync(
        TerminalWaitForChangeInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PollAsync(
            input.Timeout,
            input.AfterContentRevision,
            readScreen,
            readSession,
            (snapshot, initialRevision, _) =>
                snapshot.ContentRevision > input.AfterContentRevision
                    ? TerminalWaitOutcome.Changed(snapshot, initialRevision)
                    : null,
            cancellationToken,
            timeProvider ?? TimeProvider.System);
    }

    public static ValueTask<TerminalWaitOutcome> WaitForStableAsync(
        TerminalWaitForStableInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var clock = timeProvider ?? TimeProvider.System;
        long? lastRevision = null;
        var stableSince = 0L;
        return PollAsync(
            input.Timeout,
            initialContentRevision: null,
            readScreen,
            readSession,
            (snapshot, initialRevision, timestamp) =>
            {
                if (lastRevision != snapshot.ContentRevision)
                {
                    lastRevision = snapshot.ContentRevision;
                    stableSince = timestamp;
                    return null;
                }

                return clock.GetElapsedTime(stableSince, timestamp) >= input.StableFor
                    ? TerminalWaitOutcome.Stable(snapshot, initialRevision)
                    : null;
            },
            cancellationToken,
            clock);
    }

    private static async ValueTask<TerminalWaitOutcome> PollAsync(
        TimeSpan timeout,
        long? initialContentRevision,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        Func<TerminalScreenSnapshot, long, long, TerminalWaitOutcome?> evaluate,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(readScreen);
        ArgumentNullException.ThrowIfNull(readSession);
        ArgumentNullException.ThrowIfNull(evaluate);
        var started = timeProvider.GetTimestamp();
        TerminalScreenSnapshot? lastSnapshot = null;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return TerminalWaitOutcome.Cancelled(lastSnapshot, initialContentRevision);
            }

            TerminalScreenSnapshot snapshot;
            try
            {
                snapshot = await readScreen(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return TerminalWaitOutcome.Cancelled(lastSnapshot, initialContentRevision);
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return TerminalWaitOutcome.Cancelled(lastSnapshot, initialContentRevision);
                }

                if (await IsSessionEndedAsync(readSession, CancellationToken.None)
                        .ConfigureAwait(false))
                {
                    return TerminalWaitOutcome.SessionEnded(lastSnapshot, initialContentRevision);
                }

                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }

            initialContentRevision ??= snapshot.ContentRevision;
            lastSnapshot = snapshot;
            var now = timeProvider.GetTimestamp();
            var elapsed = timeProvider.GetElapsedTime(started, now);
            if (elapsed >= timeout)
            {
                return TerminalWaitOutcome.Timeout(lastSnapshot, initialContentRevision);
            }

            if (evaluate(snapshot, initialContentRevision.Value, now) is { } completed)
            {
                return completed;
            }

            try
            {
                if (await IsSessionEndedAsync(readSession, cancellationToken).ConfigureAwait(false))
                {
                    return TerminalWaitOutcome.SessionEnded(snapshot, initialContentRevision);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return TerminalWaitOutcome.Cancelled(lastSnapshot, initialContentRevision);
            }

            elapsed = timeProvider.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                return TerminalWaitOutcome.Timeout(lastSnapshot, initialContentRevision);
            }

            var remaining = timeout - elapsed;
            var delay = remaining < PollInterval ? remaining : PollInterval;
            try
            {
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return TerminalWaitOutcome.Cancelled(lastSnapshot, initialContentRevision);
            }
        }
    }

    private static async ValueTask<bool> IsSessionEndedAsync(
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken)
    {
        var snapshot = await readSession(cancellationToken).ConfigureAwait(false);
        return snapshot.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed;
    }
}
