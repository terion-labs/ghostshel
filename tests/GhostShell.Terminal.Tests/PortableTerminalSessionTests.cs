using System.Diagnostics;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal.Tests;

public sealed class PortableTerminalSessionTests
{
    [Fact]
    public async Task Parses_unicode_color_cursor_and_alternate_screen_into_structured_snapshot()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await harness.Pty.WriteOutputAsync("\u001b[31mred\u001b[0m 界 e\u0301\u001b[3;4H");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).PlainText.Contains("red", StringComparison.Ordinal));

        var snapshot = await session.ReadScreenAsync(default);
        Assert.Contains("red", snapshot.PlainText, StringComparison.Ordinal);
        Assert.Contains("界", snapshot.PlainText, StringComparison.Ordinal);
        Assert.Contains("e\u0301", snapshot.PlainText, StringComparison.Ordinal);
        Assert.Equal(2, snapshot.CursorRow);
        Assert.Equal(3, snapshot.CursorColumn);
        Assert.Equal(TerminalColorMode.Indexed, snapshot.StructuredRows[0].Cells[0].Foreground.Mode);
        Assert.Equal(1, snapshot.StructuredRows[0].Cells[0].Foreground.Value);
        Assert.Contains(snapshot.StructuredRows[0].Cells, cell => cell.Text == "界" && cell.Width == 2);

        await harness.Pty.WriteOutputAsync("\u001b[?1049hALT");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).IsAlternateScreen);
        Assert.Contains("ALT", (await session.ReadScreenAsync(default)).PlainText, StringComparison.Ordinal);

        await harness.Pty.WriteOutputAsync("\u001b[?1049l");
        await WaitUntilAsync(async () => !(await session.ReadScreenAsync(default)).IsAlternateScreen);
        Assert.Contains("red", (await session.ReadScreenAsync(default)).PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preserves_256_color_indexes_and_true_color_values()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await harness.Pty.WriteOutputAsync(
            "\u001b[38;5;196mI\u001b[38;2;12;34;56mR\u001b[0m");
        await WaitUntilAsync(async () =>
            (await session.ReadScreenAsync(default)).PlainText.Contains("IR", StringComparison.Ordinal));

        var cells = (await session.ReadScreenAsync(default)).StructuredRows[0].Cells;
        Assert.Equal(TerminalColorMode.Indexed, cells[0].Foreground.Mode);
        Assert.Equal(196, cells[0].Foreground.Value);
        Assert.Equal(TerminalColorMode.Rgb, cells[1].Foreground.Mode);
        Assert.Equal(0x0C2238, cells[1].Foreground.Value);
    }

    [Fact]
    public async Task Preserves_split_utf8_sequences_without_replacement_characters()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var bytes = Encoding.UTF8.GetBytes("界");

        await harness.Pty.WriteOutputBytesAsync(bytes.AsMemory(0, 1));
        await harness.Pty.WriteOutputBytesAsync(bytes.AsMemory(1));
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).PlainText.Contains("界", StringComparison.Ordinal));

        Assert.DoesNotContain("�", (await session.ReadScreenAsync(default)).PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resize_uses_explicit_cell_dimensions_for_state_and_pty()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                0,
                new ViewportDescriptor(900, 600, 1.5, Columns: 132, Rows: 43)),
            default);

        var snapshot = await session.ReadScreenAsync(default);
        Assert.Equal(132, snapshot.Columns);
        Assert.Equal(43, snapshot.Rows);
        Assert.Equal((132, 43), harness.Pty.LastResize);
    }

    [Fact]
    public async Task Key_and_mouse_input_follow_live_terminal_modes()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync("\u001b[?1h\u001b[?1000h\u001b[?1006h");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).IsMouseTrackingEnabled);

        await session.SendKeyAsync(new TerminalKeyStroke(TerminalKey.Up), default);
        await session.SendMouseAsync(
            new TerminalMouseInput(TerminalMouseButton.Left, TerminalMouseEventKind.Down, 2, 1),
            default);
        await WaitUntilAsync(() => Task.FromResult(harness.Pty.WrittenText.Contains("\u001b[<0;3;2M", StringComparison.Ordinal)));

        Assert.Contains("\u001bOA", harness.Pty.WrittenText, StringComparison.Ordinal);
        Assert.Contains("\u001b[<0;3;2M", harness.Pty.WrittenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Character_chords_use_XTerm_character_encoding()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var expected = new StringBuilder();

        for (var character = 'a'; character <= 'z'; character++)
        {
            await session.SendChordAsync(
                new TerminalCharacterChord(
                    character,
                    TerminalCharacterChordModifier.Control),
                default);
            expected.Append((char)(character - 'a' + 1));
        }

        for (var character = 'a'; character <= 'z'; character++)
        {
            await session.SendChordAsync(
                new TerminalCharacterChord(
                    character,
                    TerminalCharacterChordModifier.Alt),
                default);
            expected.Append('\u001b').Append(character);
        }

        Assert.Equal(expected.ToString(), harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Explicit_enter_and_interrupt_send_their_exact_control_sequences()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await session.EnterAsync(default);
        await session.InterruptAsync(default);
        await WaitUntilAsync(() => Task.FromResult(harness.Pty.WrittenText.Length >= 2));

        Assert.Equal("\r\u0003", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Text_and_change_waits_return_snapshots_with_monotonic_revisions()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var initial = await session.ReadScreenAsync(default);
        var textWait = session.WaitForTextAsync(
            new TerminalWaitForTextInput("AUTOMATION_READY", TimeSpan.FromSeconds(1)),
            default).AsTask();
        var changeWait = session.WaitForChangeAsync(
            new TerminalWaitForChangeInput(
                initial.ContentRevision,
                TimeSpan.FromSeconds(1)),
            default).AsTask();

        await harness.Pty.WriteOutputAsync("AUTOMATION_READY");
        var matched = await textWait;
        var changed = await changeWait;

        Assert.Equal(TerminalWaitOutcomeKind.Matched, matched.Kind);
        Assert.Contains(
            "AUTOMATION_READY",
            matched.Snapshot!.PlainText,
            StringComparison.Ordinal);
        Assert.Equal(TerminalWaitOutcomeKind.Changed, changed.Kind);
        Assert.Equal(initial.ContentRevision, changed.InitialContentRevision);
        Assert.True(changed.ObservedContentRevision > initial.ContentRevision);
    }

    [Fact]
    public async Task Wait_for_text_returns_a_typed_timeout()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        var outcome = await session.WaitForTextAsync(
            new TerminalWaitForTextInput("never appears", TimeSpan.FromMilliseconds(50)),
            default);

        Assert.Equal(TerminalWaitOutcomeKind.Timeout, outcome.Kind);
        Assert.NotNull(outcome.Snapshot);
    }

    [Fact]
    public async Task Wait_cancellation_returns_a_typed_cancelled_outcome()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        using var cancellation = new CancellationTokenSource();
        var wait = session.WaitForTextAsync(
            new TerminalWaitForTextInput("never appears", TimeSpan.FromSeconds(1)),
            cancellation.Token).AsTask();

        cancellation.Cancel();
        var outcome = await wait;

        Assert.Equal(TerminalWaitOutcomeKind.Cancelled, outcome.Kind);
    }

    [Fact]
    public async Task Wait_returns_session_ended_when_the_pty_exits_before_a_match()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        var wait = session.WaitForTextAsync(
            new TerminalWaitForTextInput("never appears", TimeSpan.FromSeconds(1)),
            default).AsTask();

        harness.Pty.Exit(0);
        var outcome = await wait;

        Assert.Equal(TerminalWaitOutcomeKind.SessionEnded, outcome.Kind);
    }

    [Fact]
    public async Task Wait_for_stable_observes_an_unchanged_revision_for_the_requested_interval()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        var outcome = await session.WaitForStableAsync(
            new TerminalWaitForStableInput(
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromSeconds(1)),
            default);

        Assert.Equal(TerminalWaitOutcomeKind.Stable, outcome.Kind);
        Assert.Equal(outcome.InitialContentRevision, outcome.ObservedContentRevision);
    }

    [Fact]
    public async Task Wait_for_stable_times_out_when_the_required_quiet_period_is_longer()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        var outcome = await session.WaitForStableAsync(
            new TerminalWaitForStableInput(
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(50)),
            default);

        Assert.Equal(TerminalWaitOutcomeKind.Timeout, outcome.Kind);
    }

    [Fact]
    public async Task Local_scrollback_is_bounded_to_primary_non_mouse_mode_and_input_returns_to_bottom()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                0,
                new ViewportDescriptor(80, 30, 1, Columns: 8, Rows: 3)),
            default);
        await harness.Pty.WriteOutputAsync(
            "line-0\r\nline-1\r\nline-2\r\nline-3\r\nline-4\r\nline-5");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).ScrollbackLinesAbove > 0);

        var bottom = await session.ReadScreenAsync(default);
        Assert.True(bottom.IsViewportAtBottom);
        Assert.True(bottom.IsCursorVisible);

        await session.ScrollViewportAsync(new TerminalViewportScrollInput(-2), default);
        var scrolled = await session.ReadScreenAsync(default);
        Assert.Equal(2, scrolled.ScrollbackLinesBelow);
        Assert.False(scrolled.IsCursorVisible);
        Assert.NotEqual(bottom.PlainText, scrolled.PlainText);

        await session.SendKeyAsync(new TerminalKeyStroke(TerminalKey.Up), default);
        Assert.True((await session.ReadScreenAsync(default)).IsViewportAtBottom);

        await harness.Pty.WriteOutputAsync("\u001b[?1000h\u001b[?1006h");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).IsMouseTrackingEnabled);
        var mouseModeTop = (await session.ReadScreenAsync(default)).ScrollbackLinesAbove;
        await session.ScrollViewportAsync(new TerminalViewportScrollInput(-1), default);
        Assert.Equal(mouseModeTop, (await session.ReadScreenAsync(default)).ScrollbackLinesAbove);

        await harness.Pty.WriteOutputAsync("\u001b[?1049hALT");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).IsAlternateScreen);
        await session.ScrollViewportAsync(new TerminalViewportScrollInput(-1), default);
        var alternate = await session.ReadScreenAsync(default);
        Assert.Equal(0, alternate.ScrollbackLinesAbove);
        Assert.Equal(0, alternate.ScrollbackLinesBelow);
    }

    [Fact]
    public async Task Clear_scrollback_erases_the_local_buffer_without_sending_remote_input()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                0,
                new ViewportDescriptor(40, 20, 1, Columns: 8, Rows: 2)),
            default);
        await harness.Pty.WriteOutputAsync("first\r\nsecond\r\nthird\r\nfourth");
        await WaitUntilAsync(async () =>
            (await session.ReadScreenAsync(default)).ScrollbackLinesAbove > 0);
        var before = await session.ReadScreenAsync(default);
        var remoteInput = harness.Pty.WrittenText;

        await session.ClearScrollbackAsync(default);

        var cleared = await session.ReadScreenAsync(default);
        Assert.DoesNotContain("first", cleared.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("fourth", cleared.PlainText, StringComparison.Ordinal);
        Assert.Equal(0, cleared.ScrollbackLinesAbove);
        Assert.True(cleared.ContentRevision > before.ContentRevision);
        Assert.Equal(remoteInput, harness.Pty.WrittenText);

        await session.ScrollViewportAsync(new TerminalViewportScrollInput(-100), default);
        Assert.Equal(0, (await session.ReadScreenAsync(default)).ScrollbackLinesAbove);
    }

    [Fact]
    public async Task Find_searches_scrollback_and_selects_a_wrapped_unicode_match_without_remote_input()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                0,
                new ViewportDescriptor(40, 20, 1, Columns: 4, Rows: 2)),
            default);
        await harness.Pty.WriteOutputAsync("界abxy\r\nfiller\r\nBXY");
        await WaitUntilAsync(async () =>
            (await session.ReadScreenAsync(default)).PlainText.Contains(
                "BXY",
                StringComparison.Ordinal));
        var remoteInput = harness.Pty.WrittenText;

        var first = await session.FindAsync(new TerminalFindInput("bxy"), default);
        var firstScreen = await session.ReadScreenAsync(default);
        var firstSelection = await session.ReadSelectionAsync(default);
        var next = await session.FindAsync(new TerminalFindInput("bxy", 1), default);

        Assert.Equal(2, first.MatchCount);
        Assert.Equal(0, first.SelectedMatchIndex);
        Assert.Contains("界ab", firstScreen.PlainText, StringComparison.Ordinal);
        Assert.Equal("bxy", firstSelection.Text, ignoreCase: true);
        Assert.Equal(1, next.SelectedMatchIndex);
        Assert.Equal("BXY", (await session.ReadSelectionAsync(default)).Text);
        Assert.Equal(remoteInput, harness.Pty.WrittenText);

        Assert.Equal(
            TerminalFindResult.Empty,
            await session.FindAsync(new TerminalFindInput(string.Empty), default));
        Assert.False((await session.ReadSelectionAsync(default)).HasSelection);
    }

    [Fact]
    public async Task Find_preserves_a_match_that_ends_beyond_the_visible_viewport()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                0,
                new ViewportDescriptor(20, 20, 1, Columns: 2, Rows: 2)),
            default);
        await harness.Pty.WriteOutputAsync("abcdef");
        await WaitUntilAsync(async () =>
            (await session.ReadScreenAsync(default)).PlainText.Contains(
                "ef",
                StringComparison.Ordinal));

        var result = await session.FindAsync(new TerminalFindInput("bcde"), default);
        var screen = await session.ReadScreenAsync(default);

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("bcde", (await session.ReadSelectionAsync(default)).Text);
        Assert.Equal(1, screen.ScrollbackLinesBelow);
        Assert.Contains(
            screen.StructuredRows.SelectMany(row => row.Cells),
            cell => cell.IsSelected);
    }

    [Fact]
    public async Task Selection_preserves_wide_cells_and_wrapped_lines_without_sending_input()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await session.AttachRendererAsync(
            new NativeRendererHost(
                "GhostShell.Managed",
                0,
                new ViewportDescriptor(40, 30, 1, Columns: 4, Rows: 3)),
            default);
        await harness.Pty.WriteOutputAsync("界abxy");
        await WaitUntilAsync(async () =>
            (await session.ReadScreenAsync(default)).PlainText.Contains("xy", StringComparison.Ordinal));
        var beforeInput = harness.Pty.WrittenText;

        await session.UpdateSelectionAsync(
            new TerminalSelectionInput(TerminalSelectionPhase.Start, Column: 1, Row: 0),
            default);
        await session.UpdateSelectionAsync(
            new TerminalSelectionInput(TerminalSelectionPhase.End, Column: 1, Row: 1),
            default);

        var selection = await session.ReadSelectionAsync(default);
        var snapshot = await session.ReadScreenAsync(default);
        Assert.True(selection.HasSelection);
        Assert.Equal("界abxy", selection.Text);
        Assert.True(snapshot.StructuredRows[0].IsWrapped);
        Assert.True(snapshot.StructuredRows[0].Cells[0].IsSelected);
        Assert.True(snapshot.StructuredRows[1].Cells[0].IsSelected);
        Assert.Equal(beforeInput, harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Osc_title_hyperlinks_and_shell_boundaries_are_typed_and_chunk_safe()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        await harness.Pty.WriteOutputAsync("\u001b]2;remote-prod\u001b\\");
        await harness.Pty.WriteOutputAsync("\u001b]8;;https://example.test/run\u001b\\go");
        await harness.Pty.WriteOutputAsync("\u001b]8;;\u001b\\ ");
        await harness.Pty.WriteOutputAsync("\u001b]133;A");
        await harness.Pty.WriteOutputAsync("\u0007$ \u001b]133;B\u0007echo\u001b]133;C\u0007\r\nout");
        await harness.Pty.WriteOutputAsync("\u001b]133;D;7\u001b\\");
        await WaitUntilAsync(async () =>
        {
            var current = await session.ReadScreenAsync(default);
            return current.WindowTitle == "remote-prod"
                && current.CommandBoundaries.Count == 4
                && current.StructuredRows[0].Cells.Any(cell => cell.Hyperlink is not null);
        });

        var snapshot = await session.ReadScreenAsync(default);
        Assert.Equal("remote-prod", snapshot.WindowTitle);
        Assert.Equal(
            [
                TerminalCommandBoundaryKind.PromptStarted,
                TerminalCommandBoundaryKind.CommandInputStarted,
                TerminalCommandBoundaryKind.CommandExecuted,
                TerminalCommandBoundaryKind.CommandFinished,
            ],
            snapshot.CommandBoundaries.Select(boundary => boundary.Kind));
        Assert.Equal(7, snapshot.CommandBoundaries[^1].ExitCode);
        Assert.All(
            snapshot.StructuredRows[0].Cells.Take(2),
            cell => Assert.Equal("https://example.test/run", cell.Hyperlink));
        Assert.Null(snapshot.StructuredRows[0].Cells[2].Hyperlink);
    }

    [Theory]
    [InlineData(TerminalClipboardAccess.Deny)]
    [InlineData(TerminalClipboardAccess.Ask)]
    [InlineData(TerminalClipboardAccess.Allow)]
    public async Task Osc52_fails_closed_without_a_system_clipboard_broker(
        TerminalClipboardAccess access)
    {
        var profile = new TerminalRenderProfileSnapshot(
            13,
            TerminalCursorStyle.Block,
            cursorBlink: false,
            10_000,
            TerminalPalette.GhostShellDark,
            clipboardPolicy: new TerminalClipboardPolicy(
                access,
                access,
                TerminalPasteSafetyPolicy.ProtectUnsafe));
        var harness = await CreateAsync(profile);
        await using var session = harness.Session;

        await harness.Pty.WriteOutputAsync("\u001b]52;c;SGVsbG8=\u0007");
        await Task.Delay(20);
        Assert.DoesNotContain("Hello", (await session.ReadScreenAsync(default)).PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("SGVsbG8=", harness.Pty.WrittenText, StringComparison.Ordinal);

        await harness.Pty.WriteOutputAsync("\u001b]52;c;?\u0007");
        await WaitUntilAsync(() => Task.FromResult(
            harness.Pty.WrittenText.Contains("\u001b]52;c;\u0007", StringComparison.Ordinal)));
        Assert.EndsWith("\u001b]52;c;\u0007", harness.Pty.WrittenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsafe_bracketed_paste_requires_confirmation_when_profile_requests_it()
    {
        var profile = new TerminalRenderProfileSnapshot(
            13,
            TerminalCursorStyle.Block,
            cursorBlink: false,
            10_000,
            TerminalPalette.GhostShellDark,
            clipboardPolicy: new TerminalClipboardPolicy(
                TerminalClipboardAccess.Ask,
                TerminalClipboardAccess.Allow,
                TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed));
        var harness = await CreateAsync(profile);
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync("\u001b[?2004h");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).IsBracketedPasteEnabled);

        var blocked = await session.PasteAsync(new TerminalPasteInput("first\nsecond"), default);
        Assert.True(blocked.RequiresConfirmation);
        Assert.False(blocked.Sent);
        Assert.DoesNotContain("first", harness.Pty.WrittenText, StringComparison.Ordinal);

        var sent = await session.PasteAsync(
            new TerminalPasteInput("first\nsecond", ConfirmedUnsafe: true),
            default);
        await WaitUntilAsync(() => Task.FromResult(harness.Pty.WrittenText.Contains("first", StringComparison.Ordinal)));
        Assert.True(sent.Sent);
        Assert.True(sent.UsedBracketedPaste);
        Assert.Contains("\u001b[200~first\nsecond\u001b[201~", harness.Pty.WrittenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paste_reports_sent_only_after_the_pty_flush_completes()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputFlushes();

        var paste = session.PasteAsync(
            new TerminalPasteInput("after-flush"),
            default).AsTask();
        try
        {
            await harness.Pty.WaitForInputFlushAttemptAsync();

            Assert.Equal("after-flush", harness.Pty.WrittenText);
            Assert.False(paste.IsCompleted);
        }
        finally
        {
            harness.Pty.ResumeInputFlushes();
        }

        var result = await paste;
        Assert.True(result.Sent);
    }

    [Fact]
    public async Task Cancellation_during_flush_preserves_the_committed_paste_receipt()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputFlushes();
        using var cancellation = new CancellationTokenSource();
        var paste = session.PasteAsync(
            new TerminalPasteInput("committed-before-cancel"),
            cancellation.Token).AsTask();
        await harness.Pty.WaitForInputFlushAttemptAsync();
        Assert.Equal("committed-before-cancel", harness.Pty.WrittenText);

        cancellation.Cancel();
        Assert.False(paste.IsCompleted);
        harness.Pty.ResumeInputFlushes();

        var result = await paste;
        Assert.True(result.Sent);
        Assert.Equal("committed-before-cancel", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Flush_failure_preserves_committed_receipt_and_fails_queued_input()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputFlushes();
        harness.Pty.FailNextInputFlush(new IOException("controlled flush failure"));
        var paste = session.PasteAsync(
            new TerminalPasteInput("committed-before-flush-failure|"),
            default).AsTask();
        await harness.Pty.WaitForInputFlushAttemptAsync();
        var queued = session.WriteAsync("must-not-run", default).AsTask();

        harness.Pty.ResumeInputFlushes();

        var result = await paste;
        var queuedFailure = await Assert.ThrowsAsync<IOException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(3)));
        await WaitUntilAsync(async () =>
            (await session.SnapshotAsync(default)).Failure?.StableCode
                == "portable_terminal_write_failed");
        Assert.True(result.Sent);
        Assert.Equal("controlled flush failure", queuedFailure.Message);
        Assert.Equal(
            "committed-before-flush-failure|",
            harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Cancelled_queued_paste_is_skipped_before_pty_delivery()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputWrites();
        var leading = session.WriteAsync("leading|", default).AsTask();
        await harness.Pty.WaitForInputWriteAttemptAsync();
        using var cancellation = new CancellationTokenSource();

        var paste = session.PasteAsync(
            new TerminalPasteInput("cancelled-paste|"),
            cancellation.Token).AsTask();
        Assert.False(paste.IsCompleted);
        cancellation.Cancel();
        Assert.False(paste.IsCompleted);

        var trailing = session.WriteAsync("trailing", default).AsTask();
        harness.Pty.ResumeInputWrites();
        await leading;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paste);
        await trailing;

        Assert.Equal("leading|trailing", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Cancelled_queued_chord_is_skipped_before_pty_delivery()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputWrites();
        var leading = session.WriteAsync("leading", default).AsTask();
        await harness.Pty.WaitForInputWriteAttemptAsync();
        using var cancellation = new CancellationTokenSource();

        var chord = session.SendChordAsync(
            new TerminalCharacterChord('a', TerminalCharacterChordModifier.Alt),
            cancellation.Token).AsTask();
        cancellation.Cancel();

        harness.Pty.ResumeInputWrites();
        await leading;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chord);

        Assert.Equal("leading", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Cancellation_reaches_an_in_flight_paste_before_pty_delivery()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputWrites();
        using var cancellation = new CancellationTokenSource();
        var paste = session.PasteAsync(
            new TerminalPasteInput("cancelled-in-flight|"),
            cancellation.Token).AsTask();
        await harness.Pty.WaitForInputWriteAttemptAsync();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paste);
        harness.Pty.ResumeInputWrites();
        await session.WriteAsync("survived", default);

        Assert.Equal("survived", harness.Pty.WrittenText);
    }

    [Fact]
    public async Task Shutdown_completes_an_in_flight_input_acknowledgement()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputWrites();
        var write = session.WriteAsync("blocked-by-shutdown", default).AsTask();
        await harness.Pty.WaitForInputWriteAttemptAsync();

        await session.CloseAsync(PanelCloseMode.Force, default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => write.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain(
            "blocked-by-shutdown",
            harness.Pty.WrittenText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pty_write_failure_completes_current_and_queued_acknowledgements()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.PauseInputWrites();
        harness.Pty.FailNextInputWrite(new IOException("controlled write failure"));
        var failing = session.WriteAsync("failing", default).AsTask();
        await harness.Pty.WaitForInputWriteAttemptAsync();
        var queued = session.WriteAsync("queued", default).AsTask();

        harness.Pty.ResumeInputWrites();

        var firstFailure = await Assert.ThrowsAsync<IOException>(
            () => failing.WaitAsync(TimeSpan.FromSeconds(3)));
        var queuedFailure = await Assert.ThrowsAsync<IOException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("controlled write failure", firstFailure.Message);
        Assert.Equal(firstFailure.Message, queuedFailure.Message);
    }

    [Fact]
    public async Task Paste_sanitizes_terminal_controls_and_normalizes_unbracketed_newlines()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        await harness.Pty.WriteOutputAsync("\u001b[?2004h");
        await WaitUntilAsync(async () => (await session.ReadScreenAsync(default)).IsBracketedPasteEnabled);
        const string unsafeControls =
            "\0\u0003\u0004\u0005\b\u000F\u0011\u0012\u0013\u0015\u0016\u0017\u001A\u001B\u001C\u007F";

        _ = await session.PasteAsync(
            new TerminalPasteInput($"before{unsafeControls}[201~\nafter", ConfirmedUnsafe: true),
            default);
        await WaitUntilAsync(() => Task.FromResult(
            harness.Pty.WrittenText.Contains("after", StringComparison.Ordinal)));

        Assert.Contains(
            $"\u001b[200~before{new string(' ', unsafeControls.Length)}[201~\nafter\u001b[201~",
            harness.Pty.WrittenText,
            StringComparison.Ordinal);

        await harness.Pty.WriteOutputAsync("\u001b[?2004l");
        await WaitUntilAsync(async () => !(await session.ReadScreenAsync(default)).IsBracketedPasteEnabled);
        var beforeUnbracketed = harness.Pty.WrittenText.Length;

        _ = await session.PasteAsync(
            new TerminalPasteInput("one\ntwo\u0003three", ConfirmedUnsafe: true),
            default);
        await WaitUntilAsync(() => Task.FromResult(
            harness.Pty.WrittenText.Length > beforeUnbracketed));

        Assert.EndsWith("one\rtwo three", harness.Pty.WrittenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_process_requires_confirmation_and_force_close_kills_it()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;

        Assert.Equal(
            PanelCloseOutcome.ConfirmationRequired,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
        Assert.Equal(0, harness.Pty.KillCount);

        Assert.Equal(
            PanelCloseOutcome.ForceTerminated,
            await session.CloseAsync(PanelCloseMode.Force, default));
        Assert.Equal(1, harness.Pty.KillCount);
        Assert.Equal(SessionLifecycle.Closed, (await session.SnapshotAsync(default)).Lifecycle);
    }

    [Fact]
    public async Task Exited_process_closes_gracefully_without_kill()
    {
        var harness = await CreateAsync();
        await using var session = harness.Session;
        harness.Pty.Exit(0);
        await WaitUntilAsync(async () => (await session.SnapshotAsync(default)).Lifecycle == SessionLifecycle.Closed);

        Assert.Equal(
            PanelCloseOutcome.GracefullyClosed,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
        Assert.Equal(0, harness.Pty.KillCount);
    }

    [Fact]
    public async Task Factory_exposes_managed_renderer_and_automation_capabilities()
    {
        var factory = new PortableTerminalSessionFactory(new FakePortablePtyFactory());

        Assert.True(factory.Capabilities.Contains(SessionCapabilities.ManagedRenderer));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalSendKeys));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalSendChord));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalEnter));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalInterrupt));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalWait));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalMouse));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalScrollback));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalClearScrollback));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalFind));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalSelection));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalPaste));

        await using var session = await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest(Environment.CurrentDirectory),
            default);
        Assert.True(session.Capabilities.Contains(SessionCapabilities.TerminalClearScrollback));
        Assert.True(session.Capabilities.Contains(SessionCapabilities.TerminalFind));
        Assert.True(session.Capabilities.Contains(SessionCapabilities.TerminalSendChord));
        Assert.Equal(SessionLifecycle.Active, (await session.SnapshotAsync(default)).Lifecycle);
    }

    [Fact]
    public void Ghostty_factory_exposes_the_same_explicit_automation_capabilities()
    {
        var factory = new GhosttyTerminalSessionFactory();

        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalSendChord));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalEnter));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalInterrupt));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.TerminalWait));
    }

    [Fact]
    public async Task Real_portable_pty_runs_a_bounded_command_on_the_current_supported_host()
    {
        var isWindows = OperatingSystem.IsWindows();
        var executable = isWindows
            ? Environment.GetEnvironmentVariable("COMSPEC")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";
        var arguments = isWindows
            ? new[] { "/d", "/c", "echo PORTABLE_PTY_SMOKE" }
            : new[] { "-c", "printf 'PORTABLE_PTY_SMOKE\\n'" };
        var factory = new PortableTerminalSessionFactory();
        await using var session = await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest(Environment.CurrentDirectory, executable, arguments),
            default);

        await WaitUntilAsync(async () =>
            (await session.ReadScreenAsync(default)).PlainText.Contains(
                "PORTABLE_PTY_SMOKE",
                StringComparison.Ordinal));
        await WaitUntilAsync(async () =>
            (await session.SnapshotAsync(default)).Lifecycle == SessionLifecycle.Closed);

        Assert.Equal(
            PanelCloseOutcome.GracefullyClosed,
            await session.CloseAsync(PanelCloseMode.Graceful, default));
    }

    private static async Task<TerminalHarness> CreateAsync(TerminalRenderProfileSnapshot? profile = null)
    {
        var ptyFactory = new FakePortablePtyFactory();
        var factory = new PortableTerminalSessionFactory(ptyFactory);
        var session = await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest(Environment.CurrentDirectory, renderProfile: profile),
            default);
        return new TerminalHarness((PortableTerminalSession)session, ptyFactory.Connection);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(3))
            {
                throw new TimeoutException("The portable terminal did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed record TerminalHarness(
        PortableTerminalSession Session,
        FakePortablePtyConnection Pty);
}
