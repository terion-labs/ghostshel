using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Protocol;

namespace GhostShell.SessionHost.Tests;

public sealed class TerminalOperationTests
{
    [Fact]
    public async Task TerminalWriteRequiresCurrentLease()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var denied = await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(harness.SessionId, new("unknown-lease"), "pwd\n"),
            harness.HumanContext(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.LeaseDenied, denied.Error().Code);

        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        _ = (await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(harness.SessionId, lease.Id, "pwd\n"),
            harness.HumanContext(),
            CancellationToken.None)).Value();
    }

    [Fact]
    public async Task TerminalWriteReplaysASequentialDuplicateWithoutRepeatingInput()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        var context = harness.HumanContext(
            idempotencyKey: new GhostShell.Core.IdempotencyKey("startup-command-batch"));
        var request = new TerminalWriteRequest(harness.SessionId, lease.Id, "deploy\n");

        var first = await harness.Client.WriteTerminalAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await harness.Client.WriteTerminalAsync(
            request,
            context,
            CancellationToken.None);

        Assert.IsType<HostResult<Unit>.Success>(first);
        Assert.IsType<HostResult<Unit>.Success>(replay);
        Assert.Equal(1, harness.Factory[harness.SessionId].WriteCount);
        Assert.Equal("deploy\n", harness.Factory[harness.SessionId].LastWrittenText);
    }

    [Fact]
    public async Task TerminalWriteReplaysAcrossAReplacedLeaseOnTheSameSession()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var firstLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        var context = harness.HumanContext(
            idempotencyKey: new GhostShell.Core.IdempotencyKey("renderer-recreated-batch"));
        _ = (await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(harness.SessionId, firstLease.Id, "deploy\n"),
            context,
            CancellationToken.None)).Value();
        _ = (await harness.Client.ReleaseInputLeaseAsync(
            new ReleaseInputLeaseRequest(harness.SessionId, firstLease.Id),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var replacementLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;

        var replay = await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(harness.SessionId, replacementLease.Id, "deploy\n"),
            context,
            CancellationToken.None);

        Assert.NotEqual(firstLease.Id, replacementLease.Id);
        Assert.IsType<HostResult<Unit>.Success>(replay);
        Assert.Equal(1, harness.Factory[harness.SessionId].WriteCount);
    }

    [Fact]
    public async Task ConcurrentIdempotentTerminalWritesAreSerializedBeforeReplayCheck()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        var terminal = harness.Factory[harness.SessionId];
        terminal.BlockWrites = true;
        var context = harness.HumanContext(
            idempotencyKey: new GhostShell.Core.IdempotencyKey("concurrent-startup-batch"));
        var request = new TerminalWriteRequest(harness.SessionId, lease.Id, "deploy\n");

        var first = harness.Client.WriteTerminalAsync(
            request,
            context,
            CancellationToken.None).AsTask();
        await terminal.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = harness.Client.WriteTerminalAsync(
            request,
            context,
            CancellationToken.None).AsTask();

        Assert.False(duplicate.IsCompleted);
        Assert.Equal(1, terminal.WriteCount);
        terminal.ReleaseWrite.TrySetResult();
        var results = await Task.WhenAll(first, duplicate);

        Assert.Equal(1, terminal.WriteCount);
        Assert.All(results, result => Assert.IsType<HostResult<Unit>.Success>(result));
    }

    [Fact]
    public async Task TerminalWriteRejectsAnIdempotencyKeyReusedForDifferentText()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        var context = harness.HumanContext(
            idempotencyKey: new GhostShell.Core.IdempotencyKey("startup-command-batch-reused"));

        _ = (await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(harness.SessionId, lease.Id, "first\n"),
            context,
            CancellationToken.None)).Value();
        var reused = await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(harness.SessionId, lease.Id, "second\n"),
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.IdempotencyKeyReused, reused.Error().Code);
        Assert.Equal(1, harness.Factory[harness.SessionId].WriteCount);
    }

    [Fact]
    public async Task TerminalWriteRejectsAnIdempotencyKeyReusedForAnotherSession()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var otherSessionId = new GhostShell.Core.SessionId("session-2");
        await harness.OpenAsync(
            sessionId: otherSessionId,
            panelId: new GhostShell.Core.PanelInstanceId("panel-2"));
        var firstLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        var otherLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(otherSessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        var context = harness.HumanContext(
            idempotencyKey: new GhostShell.Core.IdempotencyKey("startup-command-wrong-session"));

        _ = (await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(harness.SessionId, firstLease.Id, "deploy\n"),
            context,
            CancellationToken.None)).Value();
        var reused = await harness.Client.WriteTerminalAsync(
            new TerminalWriteRequest(otherSessionId, otherLease.Id, "deploy\n"),
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.IdempotencyKeyReused, reused.Error().Code);
        Assert.Equal(1, harness.Factory[harness.SessionId].WriteCount);
        Assert.Equal(0, harness.Factory[otherSessionId].WriteCount);
    }

    [Fact]
    public async Task Structured_terminal_inputs_require_the_current_lease()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var unknownLease = new GhostShell.Core.InputLeaseId("unknown-lease");

        var deniedKey = await harness.Client.SendTerminalKeyAsync(
            new TerminalKeyRequest(
                harness.SessionId,
                unknownLease,
                new TerminalKeyStroke(TerminalKey.Enter)),
            harness.HumanContext(),
            default);
        var deniedPhysicalKey = await harness.Client.SendTerminalPhysicalKeyAsync(
            new TerminalPhysicalKeyRequest(
                harness.SessionId,
                unknownLease,
                PhysicalKey()),
            harness.HumanContext(),
            default);
        var deniedMouse = await harness.Client.SendTerminalMouseAsync(
            new TerminalMouseRequest(
                harness.SessionId,
                unknownLease,
                new TerminalMouseInput(TerminalMouseButton.Left, TerminalMouseEventKind.Down, 0, 0)),
            harness.HumanContext(),
            default);
        var deniedPaste = await harness.Client.PasteTerminalAsync(
            new TerminalPasteRequest(
                harness.SessionId,
                unknownLease,
                new TerminalPasteInput("safe")),
            harness.HumanContext(),
            default);
        var deniedScroll = await harness.Client.ScrollTerminalViewportAsync(
            new TerminalViewportScrollRequest(
                harness.SessionId,
                unknownLease,
                new TerminalViewportScrollInput(-3)),
            harness.HumanContext(),
            default);
        var deniedSelection = await harness.Client.UpdateTerminalSelectionAsync(
            new TerminalSelectionRequest(
                harness.SessionId,
                unknownLease,
                new TerminalSelectionInput(TerminalSelectionPhase.Start, 0, 0)),
            harness.HumanContext(),
            default);
        var deniedSelectionRead = await harness.Client.ReadTerminalSelectionAsync(
            new TerminalSelectionReadRequest(harness.SessionId, unknownLease),
            harness.HumanContext(),
            default);

        Assert.Equal(HostErrorCode.LeaseDenied, deniedKey.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedPhysicalKey.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedMouse.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedPaste.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedScroll.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedSelection.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedSelectionRead.Error().Code);

        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            default)).Value().Lease!;
        var key = new TerminalKeyStroke(TerminalKey.F4, TerminalKeyModifiers.Alt);
        var physicalKey = PhysicalKey();
        var mouse = new TerminalMouseInput(TerminalMouseButton.Left, TerminalMouseEventKind.Down, 4, 2);
        var paste = new TerminalPasteInput("safe");
        var scroll = new TerminalViewportScrollInput(-3);
        var selection = new TerminalSelectionInput(TerminalSelectionPhase.Start, 4, 2);

        _ = (await harness.Client.SendTerminalKeyAsync(
            new TerminalKeyRequest(harness.SessionId, lease.Id, key),
            harness.HumanContext(),
            default)).Value();
        _ = (await harness.Client.SendTerminalPhysicalKeyAsync(
            new TerminalPhysicalKeyRequest(harness.SessionId, lease.Id, physicalKey),
            harness.HumanContext(),
            default)).Value();
        _ = (await harness.Client.SendTerminalMouseAsync(
            new TerminalMouseRequest(harness.SessionId, lease.Id, mouse),
            harness.HumanContext(),
            default)).Value();
        var pasteResult = (await harness.Client.PasteTerminalAsync(
            new TerminalPasteRequest(harness.SessionId, lease.Id, paste),
            harness.HumanContext(),
            default)).Value();
        _ = (await harness.Client.ScrollTerminalViewportAsync(
            new TerminalViewportScrollRequest(harness.SessionId, lease.Id, scroll),
            harness.HumanContext(),
            default)).Value();
        _ = (await harness.Client.UpdateTerminalSelectionAsync(
            new TerminalSelectionRequest(harness.SessionId, lease.Id, selection),
            harness.HumanContext(),
            default)).Value();
        var selectedText = (await harness.Client.ReadTerminalSelectionAsync(
            new TerminalSelectionReadRequest(harness.SessionId, lease.Id),
            harness.HumanContext(),
            default)).Value();

        Assert.Equal(key, harness.Factory[harness.SessionId].LastKeyStroke);
        Assert.Equal(physicalKey, harness.Factory[harness.SessionId].LastPhysicalKeyEvent);
        Assert.Equal(mouse, harness.Factory[harness.SessionId].LastMouseInput);
        Assert.Equal(paste, harness.Factory[harness.SessionId].LastPasteInput);
        Assert.Equal(scroll, harness.Factory[harness.SessionId].LastScrollInput);
        Assert.Equal(selection, harness.Factory[harness.SessionId].LastSelectionInput);
        Assert.Equal("selected", selectedText.Text);
        Assert.True(pasteResult.Sent);
    }

    [Fact]
    public async Task Terminal_focus_gain_and_loss_reach_the_renderer_port()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();

        _ = (await harness.Client.FocusTerminalAsync(
            harness.SessionId,
            harness.HumanContext(),
            default)).Value();
        _ = (await harness.Client.BlurTerminalAsync(
            harness.SessionId,
            harness.HumanContext(),
            default)).Value();

        var terminal = harness.Factory[harness.SessionId];
        Assert.Equal(1, terminal.FocusCount);
        Assert.Equal(1, terminal.BlurCount);
    }

    private static TerminalPhysicalKeyEvent PhysicalKey() => new(
        TerminalPhysicalKey.A,
        "A",
        "a",
        TerminalKeyModifiers.None,
        TerminalKeyModifiers.None,
        TerminalKeyAction.Press,
        'a');

    [Fact]
    public async Task Buffer_commands_require_a_lease_and_reach_the_typed_terminal_engine()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var unknownLease = new GhostShell.Core.InputLeaseId("unknown-lease");

        var deniedClear = await harness.Client.ClearTerminalScrollbackAsync(
            new TerminalClearScrollbackRequest(harness.SessionId, unknownLease),
            harness.HumanContext(),
            default);
        var deniedFind = await harness.Client.FindTerminalAsync(
            new TerminalFindRequest(
                harness.SessionId,
                unknownLease,
                new TerminalFindInput("needle")),
            harness.HumanContext(),
            default);

        Assert.Equal(HostErrorCode.LeaseDenied, deniedClear.Error().Code);
        Assert.Equal(HostErrorCode.LeaseDenied, deniedFind.Error().Code);

        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            default)).Value().Lease!;
        _ = (await harness.Client.ClearTerminalScrollbackAsync(
            new TerminalClearScrollbackRequest(harness.SessionId, lease.Id),
            harness.HumanContext(),
            default)).Value();
        var found = (await harness.Client.FindTerminalAsync(
            new TerminalFindRequest(
                harness.SessionId,
                lease.Id,
                new TerminalFindInput("needle", 1)),
            harness.HumanContext(),
            default)).Value();

        var terminal = harness.Factory[harness.SessionId];
        Assert.Equal(1, terminal.ClearScrollbackCount);
        Assert.Equal(new TerminalFindInput("needle", 1), terminal.LastFindInput);
        Assert.Equal(2, found.MatchCount);
        Assert.Equal(1, found.SelectedMatchIndex);

        var hello = (await harness.Client.NegotiateAsync(
            new ClientHello([ProtocolVersions.Current], SessionHostTestHarness.AllCapabilities()),
            harness.HumanContext(),
            default)).Value();
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.TerminalClearScrollback));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.TerminalFind));
    }

    [Fact]
    public async Task RendererCanDetachAndReattachWithoutClosingPty()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var first = await harness.AttachAsync();
        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                first.Attachment.Id,
                new NativeRendererHost("fake", 1, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        _ = (await harness.Client.DetachAsync(
            new DetachSessionRequest(first.Attachment.Id, harness.SessionId),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        Assert.False(harness.Factory[harness.SessionId].IsClosed);
        Assert.Equal(1, harness.Factory[harness.SessionId].DetachRendererCount);

        var second = await harness.AttachAsync();
        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                second.Attachment.Id,
                new NativeRendererHost("fake", 2, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(2, harness.Factory[harness.SessionId].AttachRendererCount);
        Assert.False(harness.Factory[harness.SessionId].IsClosed);
    }

    [Fact]
    public async Task RendererHostPreservesTheSynchronousApplicationKeyInterceptor()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var attachment = await harness.AttachAsync();
        NativeRendererKeyInput? observed = null;
        var renderer = new NativeRendererHost(
            "fake",
            1,
            ViewportDescriptor.Empty,
            input =>
            {
                observed = input;
                return true;
            });

        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                attachment.Attachment.Id,
                renderer),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var forwarded = harness.Factory[harness.SessionId].LastRendererHost;
        Assert.NotNull(forwarded?.KeyInterceptor);
        var input = new NativeRendererKeyInput(
            new GhostShell.Core.KeyStroke("B", GhostShell.Core.KeyModifiers.Control),
            IsRepeat: false);
        Assert.True(forwarded.KeyInterceptor(input));
        Assert.Equal(input, observed);
    }

    [Fact]
    public async Task RendererHostBindsPhysicalInputToTheExactHumanAttachment()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var attachment = await harness.AttachAsync();
        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                attachment.Attachment.Id,
                new NativeRendererHost("fake", 1, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var agentLease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(
                harness.SessionId,
                AttachmentId: null,
                Duration: TimeSpan.FromMinutes(1)),
            SessionHostTestHarness.AgentContext(),
            CancellationToken.None)).Value();
        Assert.True(agentLease.Granted);

        var forwarded = harness.Factory[harness.SessionId].LastRendererHost;
        Assert.NotNull(forwarded?.PhysicalInputGate);
        Assert.True(forwarded.PhysicalInputGate(
            new NativeRendererPhysicalInput(
                NativeRendererPhysicalInputKind.ImeCommit)));

        var snapshot = (await harness.Client.GetSnapshotAsync(
            harness.SessionId,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(ActorKind.Human, snapshot.InputLease?.Holder.Kind);
        Assert.Equal(harness.ClientId, snapshot.InputLease?.Holder.ClientId);
        Assert.Equal(attachment.Attachment.Id, snapshot.InputLease?.AttachmentId);

        _ = (await harness.Client.DetachAsync(
            new DetachSessionRequest(
                attachment.Attachment.Id,
                harness.SessionId),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.False(forwarded.PhysicalInputGate(
            new NativeRendererPhysicalInput(
                NativeRendererPhysicalInputKind.KeyDown)));
    }

    [Fact]
    public async Task RendererPhysicalInputGateRejectsAContextFromAnotherClient()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var attachment = await harness.AttachAsync();

        var result = await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                attachment.Attachment.Id,
                new NativeRendererHost("fake", 1, ViewportDescriptor.Empty)),
            harness.HumanContext(new ClientId("different-client")),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.LeaseDenied, result.Error().Code);
        Assert.Equal(0, harness.Factory[harness.SessionId].AttachRendererCount);
    }

    [Fact]
    public async Task SameClientInteractiveReattachTransfersRendererOwnership()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var first = await harness.AttachAsync();
        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                first.Attachment.Id,
                new NativeRendererHost("fake", 1, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var second = await harness.AttachAsync();

        Assert.NotEqual(first.Attachment.Id, second.Attachment.Id);
        Assert.Single(second.Snapshot.Attachments);
        Assert.Equal(second.Attachment.Id, second.Snapshot.Attachments[0].Id);
        Assert.Equal(1, harness.Factory[harness.SessionId].DetachRendererCount);

        _ = (await harness.Client.AttachTerminalRendererAsync(
            new AttachTerminalRendererRequest(
                harness.SessionId,
                second.Attachment.Id,
                new NativeRendererHost("fake", 2, ViewportDescriptor.Empty)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        _ = (await harness.Client.DetachAsync(
            new DetachSessionRequest(first.Attachment.Id, harness.SessionId),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        Assert.True(harness.Factory[harness.SessionId].RendererAttached);
        Assert.Equal(1, harness.Factory[harness.SessionId].DetachRendererCount);
    }
}
