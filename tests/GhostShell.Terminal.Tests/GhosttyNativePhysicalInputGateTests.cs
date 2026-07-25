using GhostShell.Application;

namespace GhostShell.Terminal.Tests;

public sealed class GhosttyNativePhysicalInputGateTests
{
    [Theory]
    [InlineData(NativeRendererPhysicalInputKind.KeyDown)]
    [InlineData(NativeRendererPhysicalInputKind.KeyUp)]
    [InlineData(NativeRendererPhysicalInputKind.ModifiersChanged)]
    [InlineData(NativeRendererPhysicalInputKind.ImePreedit)]
    [InlineData(NativeRendererPhysicalInputKind.ImeCommit)]
    [InlineData(NativeRendererPhysicalInputKind.Paste)]
    [InlineData(NativeRendererPhysicalInputKind.MouseMove)]
    [InlineData(NativeRendererPhysicalInputKind.MouseDrag)]
    [InlineData(NativeRendererPhysicalInputKind.MouseButtonDown)]
    [InlineData(NativeRendererPhysicalInputKind.MouseButtonUp)]
    [InlineData(NativeRendererPhysicalInputKind.MouseScroll)]
    public void PhysicalInputKindsMapWithoutLosingTheirCategory(
        NativeRendererPhysicalInputKind kind)
    {
        var native = new NativeTerminalPhysicalInputEventV1(
            (uint)kind,
            authorityEpoch: 42);

        Assert.True(GhosttyNativePhysicalInputGate.TryMap(native, out var input));
        Assert.Equal(kind, input.Kind);
    }

    [Fact]
    public void InvalidPhysicalInputEventsFailClosed()
    {
        var futureVersion = new NativeTerminalPhysicalInputEventV1(
            (uint)NativeRendererPhysicalInputKind.KeyDown,
            authorityEpoch: 1,
            version: 2);
        var zeroEpoch = new NativeTerminalPhysicalInputEventV1(
            (uint)NativeRendererPhysicalInputKind.KeyDown,
            authorityEpoch: 0);
        var unknownKind = new NativeTerminalPhysicalInputEventV1(
            kind: uint.MaxValue,
            authorityEpoch: 1);

        Assert.False(GhosttyNativePhysicalInputGate.TryMap(futureVersion, out _));
        Assert.False(GhosttyNativePhysicalInputGate.TryMap(zeroEpoch, out _));
        Assert.False(GhosttyNativePhysicalInputGate.TryMap(unknownKind, out _));
    }

    [Fact]
    public void ReverseCallbackFailuresAndDisposedRegistrationsFailClosed()
    {
        NativeTerminalPhysicalInputGateV1 throwing = (
            nint _,
            in NativeTerminalPhysicalInputEventV1 _) =>
            throw new InvalidOperationException("simulated gate failure");
        var registration =
            GhosttyNativePhysicalInputCallbackRegistry.Register(throwing);
        var input = new NativeTerminalPhysicalInputEventV1(
            (uint)NativeRendererPhysicalInputKind.KeyDown,
            authorityEpoch: 1);

        Assert.False(GhosttyNativePhysicalInputCallbackRegistry.NativeCallback(
            registration.Id,
            in input));

        registration.Dispose();
        Assert.False(GhosttyNativePhysicalInputCallbackRegistry.NativeCallback(
            registration.Id,
            in input));
    }
}
