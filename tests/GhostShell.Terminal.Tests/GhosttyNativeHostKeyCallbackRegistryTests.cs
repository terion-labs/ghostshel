using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GhostShell.Terminal.Tests;

public sealed class GhosttyNativeHostKeyCallbackRegistryTests
{
    private static readonly NativeTerminalHostKeyEventV1 KeyEvent = new(
        physicalKey: 11,
        codepoint: 'b',
        modifiers: 1U << 2,
        isRepeat: false);

    [Fact]
    public void Static_native_callback_has_process_lifetime_identity()
    {
        var callback = GhosttyNativeHostKeyCallbackRegistry.NativeCallback;
        var before = Marshal.GetFunctionPointerForDelegate(callback);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var after = Marshal.GetFunctionPointerForDelegate(
            GhosttyNativeHostKeyCallbackRegistry.NativeCallback);
        Assert.Same(callback, GhosttyNativeHostKeyCallbackRegistry.NativeCallback);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Disposed_registration_immediately_passes_through()
    {
        var invocationCount = 0;
        NativeTerminalHostKeyInterceptorV1 callback = (
            nint _,
            in NativeTerminalHostKeyEventV1 _) =>
        {
            invocationCount++;
            return true;
        };
        var registration = GhosttyNativeHostKeyCallbackRegistry.Register(callback);

        Assert.True(GhosttyNativeHostKeyCallbackRegistry.NativeCallback(
            registration.Id,
            in KeyEvent));

        registration.Dispose();

        Assert.False(GhosttyNativeHostKeyCallbackRegistry.NativeCallback(
            registration.Id,
            in KeyEvent));
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void Abandoned_registration_becomes_safe_pass_through_after_finalization()
    {
        var abandoned = CreateAbandonedRegistration();

        CollectUntilDead(abandoned.Registration);

        Assert.False(abandoned.Registration.IsAlive);
        Assert.False(abandoned.Handler.IsAlive);
        Assert.False(GhosttyNativeHostKeyCallbackRegistry.NativeCallback(
            abandoned.Id,
            in KeyEvent));
    }

    [Fact]
    public async Task In_flight_callback_finishes_once_while_disposal_blocks_future_calls()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var invocationCount = 0;
        NativeTerminalHostKeyInterceptorV1 callback = (
            nint _,
            in NativeTerminalHostKeyEventV1 _) =>
        {
            Interlocked.Increment(ref invocationCount);
            entered.Set();
            return release.Wait(TimeSpan.FromSeconds(5));
        };
        using var registration = GhosttyNativeHostKeyCallbackRegistry.Register(callback);

        var invocation = Task.Run(() =>
            GhosttyNativeHostKeyCallbackRegistry.NativeCallback(
                registration.Id,
                in KeyEvent));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        registration.Dispose();
        release.Set();

        Assert.True(await invocation);
        Assert.False(GhosttyNativeHostKeyCallbackRegistry.NativeCallback(
            registration.Id,
            in KeyEvent));
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void Callback_exceptions_fail_closed_at_the_native_boundary()
    {
        NativeTerminalHostKeyInterceptorV1 callback = (
            nint _,
            in NativeTerminalHostKeyEventV1 _) =>
            throw new InvalidOperationException("Synthetic callback failure.");
        using var registration = GhosttyNativeHostKeyCallbackRegistry.Register(callback);

        Assert.False(GhosttyNativeHostKeyCallbackRegistry.NativeCallback(
            registration.Id,
            in KeyEvent));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AbandonedRegistration CreateAbandonedRegistration()
    {
        var handler = new CallbackHandler();
        NativeTerminalHostKeyInterceptorV1 callback = handler.Invoke;
        var registration = GhosttyNativeHostKeyCallbackRegistry.Register(callback);
        return new AbandonedRegistration(
            registration.Id,
            new WeakReference(registration),
            new WeakReference(handler));
    }

    private static void CollectUntilDead(WeakReference reference)
    {
        for (var attempt = 0; attempt < 8 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    private sealed class CallbackHandler
    {
        public bool Invoke(nint userdata, in NativeTerminalHostKeyEventV1 keyEvent)
        {
            _ = userdata;
            _ = keyEvent;
            return true;
        }
    }

    private sealed record AbandonedRegistration(
        nint Id,
        WeakReference Registration,
        WeakReference Handler);
}
