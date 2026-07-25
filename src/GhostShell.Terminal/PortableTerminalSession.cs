using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using GhostShell.Application;
using GhostShell.Core;
using XTerm.Buffer;
using XTerm.Input;
using XTerm.Options;
using XTerminal = XTerm.Terminal;
using XKey = XTerm.Input.Key;
using XKeyModifiers = XTerm.Input.KeyModifiers;
using XMouseButton = XTerm.Input.MouseButton;
using XMouseEventType = XTerm.Input.MouseEventType;

namespace GhostShell.Terminal;

internal sealed class PortableTerminalSession : ITerminalPanelSession
{
    private const int MaximumCapturedCells = 262_144;
    private const int MaximumPlainTextCharacters = 1_048_576;
    private const int MaximumCommandBoundaries = 4_096;
    private const int MaximumFindCharacters = 4 * 1024 * 1024;
    private const int MaximumFindMatches = 4_096;
    private readonly object _gate = new();
    private readonly TerminalLaunchRequest _launch;
    private readonly IPortablePtyConnection _pty;
    private readonly XTerminal _terminal;
    private readonly PortableTerminalOscParser _oscParser = new();
    private readonly Dictionary<BufferLine, Dictionary<int, HyperlinkCellStamp>> _hyperlinks =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<CommandBoundaryMarker> _commandBoundaries = [];
    private SelectionEndpoint? _selectionStart;
    private SelectionEndpoint? _selectionEnd;
    private readonly Dictionary<TerminalBuffer, BufferLine> _clearedScrollbackBoundaries =
        new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource _lifetime = new();
    // The queue is the portable engine's input-authority barrier: one caller token
    // follows each mutation until its PTY write and flush are acknowledged.
    private readonly Channel<QueuedTerminalInput> _writes = Channel.CreateBounded<QueuedTerminalInput>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Channel<PanelSessionEvent> _events = Channel.CreateBounded<PanelSessionEvent>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SessionFailure? _failure;
    private bool _rendererAttached;
    private bool _processExited;
    private bool _closed;
    private int? _exitCode;
    private long _sequence;
    private long _contentRevision;
    private long _commandBoundarySequence;

    public PortableTerminalSession(
        SessionId id,
        TerminalLaunchRequest launch,
        IPortablePtyConnection pty,
        int initialColumns,
        int initialRows)
    {
        Id = id;
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _pty = pty ?? throw new ArgumentNullException(nameof(pty));
        var profile = launch.RenderProfile;
        _terminal = new XTerminal(
            new TerminalOptions
            {
                Cols = initialColumns,
                Rows = initialRows,
                Scrollback = profile?.ScrollbackLines ?? 10_000,
                CursorBlink = profile?.CursorBlink ?? false,
                CursorStyle = MapCursorStyle(profile?.CursorStyle ?? TerminalCursorStyle.Block),
                FontFamily = profile?.FontFamily ?? "monospace",
                FontSize = (int)Math.Round(profile?.FontSize ?? 13),
                LineHeight = profile?.LineHeight ?? 1,
                TermName = "xterm-256color",
            });
        _terminal.DataReceived += OnTerminalDataReceived;
        lock (_gate)
        {
            PublishUnsafe(SessionLifecycle.Active, SessionHealth.Healthy, "Portable PTY and terminal state started.");
        }

        _pty.ProcessExited += OnProcessExited;
        _readerTask = ReadLoopAsync(_lifetime.Token);
        _writerTask = WriteLoopAsync(_lifetime.Token);
        TryMarkKnownProcessExit();
    }

    public SessionId Id { get; }

    public TerminalLaunchRequest Launch => _launch;

    public PanelKind Kind => PanelKind.Terminal;

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.ManagedRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalMouse,
        SessionCapabilities.TerminalScrollback,
        SessionCapabilities.TerminalClearScrollback,
        SessionCapabilities.TerminalFind,
        SessionCapabilities.TerminalSelection,
        SessionCapabilities.TerminalPaste,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalFocus,
    ]);

    public ValueTask AttachRendererAsync(
        NativeRendererHost rendererHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rendererHost);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(rendererHost.HandleDescriptor, "GhostShell.Managed", StringComparison.Ordinal))
        {
            return ValueTask.FromException(new PlatformNotSupportedException(
                "The portable terminal expects the GhostShell managed renderer."));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _rendererAttached = true;
            ResizeUnsafe(rendererHost.Viewport);
            PublishUnsafe(SessionLifecycle.Active, SessionHealth.Healthy, "Managed terminal renderer attached.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DetachRendererAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _rendererAttached = false;
            if (!_closed)
            {
                PublishUnsafe(SessionLifecycle.Active, SessionHealth.Healthy, "Managed renderer detached.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string response;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            response = _terminal.GenerateFocusEvent(focused: true);
        }

        return QueueInputAsync(response, cancellationToken);
    }

    public ValueTask ResizeAsync(ViewportDescriptor viewport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ResizeUnsafe(viewport);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
        }

        return QueueInputAsync(text, cancellationToken);
    }

    public ValueTask SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyStroke);
        string sequence;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            sequence = _terminal.GenerateKeyInput(MapKey(keyStroke.Key), MapModifiers(keyStroke.Modifiers));
        }

        return QueueInputAsync(sequence, cancellationToken);
    }

    public ValueTask SendChordAsync(
        TerminalCharacterChord chord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chord);
        string sequence;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            sequence = _terminal.GenerateCharInput(
                chord.Character,
                MapChordModifier(chord.Modifier));
            if (sequence.Length == 0)
            {
                throw new InvalidOperationException(
                    "XTerm.NET did not encode the terminal character chord.");
            }
        }

        return QueueInputAsync(sequence, cancellationToken);
    }

    public ValueTask EnterAsync(CancellationToken cancellationToken) =>
        SendKeyAsync(new TerminalKeyStroke(TerminalKey.Enter), cancellationToken);

    public ValueTask InterruptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
        }

        return QueueInputAsync("\u0003", cancellationToken);
    }

    public ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollInput);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_terminal.IsAlternateBufferActive
                || _terminal.MouseTrackingMode != MouseTrackingMode.None)
            {
                return ValueTask.CompletedTask;
            }

            var before = _terminal.Buffer.YDisp;
            _terminal.ScrollLines(scrollInput.Lines);
            var floor = ClearedScrollbackFloorUnsafe(_terminal.Buffer);
            if (_terminal.Buffer.YDisp < floor)
            {
                _terminal.Buffer.ViewportY = floor;
            }

            if (_terminal.Buffer.YDisp != before)
            {
                _contentRevision++;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ClearScrollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            // XTerm.NET owns the canonical managed buffer. Clear() erases both its
            // viewport and saved lines without sending input to the remote process.
            _terminal.Clear();
            _terminal.ScrollToBottom();
            var buffer = _terminal.Buffer;
            foreach (var line in buffer.Lines.GetItems())
            {
                line.IsWrapped = false;
            }

            _clearedScrollbackBoundaries[buffer] = buffer.Lines[buffer.YBase]
                ?? throw new InvalidOperationException(
                    "The cleared terminal viewport is unavailable.");
            _terminal.Selection.ClearSelection();
            _selectionStart = null;
            _selectionEnd = null;
            _hyperlinks.Clear();
            _commandBoundaries.Clear();
            _contentRevision++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<TerminalFindResult> FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (input.Query.Length == 0)
            {
                ClearSelectionUnsafe();
                return ValueTask.FromResult(TerminalFindResult.Empty);
            }

            var matches = FindMatchesUnsafe(input.Query, out var truncated);
            if (matches.Count == 0)
            {
                ClearSelectionUnsafe();
                return ValueTask.FromResult(new TerminalFindResult(0, -1, truncated));
            }

            var selectedIndex = (int)(((long)input.RequestedMatchIndex % matches.Count
                + matches.Count) % matches.Count);
            SelectFindMatchUnsafe(matches[selectedIndex]);
            return ValueTask.FromResult(new TerminalFindResult(
                matches.Count,
                selectedIndex,
                truncated));
        }
    }

    public ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectionInput);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var column = Math.Clamp(selectionInput.Column, 0, _terminal.Cols - 1);
            var row = Math.Clamp(selectionInput.Row, 0, _terminal.Rows - 1);
            column = NormalizeWideCellColumnUnsafe(column, row);
            var endpoint = CreateSelectionEndpointUnsafe(column, row);
            switch (selectionInput.Phase)
            {
                case TerminalSelectionPhase.Start:
                    _terminal.Selection.StartSelection(column, row);
                    _selectionStart = endpoint;
                    _selectionEnd = endpoint;
                    break;
                case TerminalSelectionPhase.Update:
                    _terminal.Selection.UpdateSelection(column, row);
                    if (_selectionStart is not null)
                    {
                        _selectionEnd = endpoint;
                    }
                    break;
                case TerminalSelectionPhase.End:
                    _terminal.Selection.UpdateSelection(column, row);
                    _terminal.Selection.EndSelection();
                    if (_selectionStart is not null)
                    {
                        _selectionEnd = endpoint;
                    }
                    break;
                case TerminalSelectionPhase.Clear:
                    _terminal.Selection.ClearSelection();
                    _selectionStart = null;
                    _selectionEnd = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(selectionInput),
                        selectionInput.Phase,
                        "Unknown terminal selection phase.");
            }

            _contentRevision++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<TerminalSelectionText> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_selectionStart is not { } start || _selectionEnd is not { } end)
            {
                return ValueTask.FromResult(new TerminalSelectionText(string.Empty, false, false));
            }

            var selected = BuildSelectionTextUnsafe(start, end);
            if (selected is null)
            {
                return ValueTask.FromResult(new TerminalSelectionText(string.Empty, false, false));
            }

            var truncated = selected.Length > TerminalSelectionText.MaximumCharacters;
            if (truncated)
            {
                var length = TerminalSelectionText.MaximumCharacters;
                if (length > 0
                    && char.IsHighSurrogate(selected[length - 1])
                    && length < selected.Length
                    && char.IsLowSurrogate(selected[length]))
                {
                    length--;
                }

                selected = selected[..length];
            }

            return ValueTask.FromResult(new TerminalSelectionText(selected, true, truncated));
        }
    }

    public ValueTask SendMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mouseInput);
        string sequence;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            sequence = _terminal.GenerateMouseEvent(
                MapMouseButton(mouseInput.Button),
                mouseInput.Column,
                mouseInput.Row,
                MapMouseKind(mouseInput.Kind),
                MapModifiers(mouseInput.Modifiers));
        }

        return QueueInputAsync(sequence, cancellationToken);
    }

    public async ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pasteInput);
        bool bracketed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            bracketed = _terminal.BracketedPasteMode;
        }

        var policy = _launch.RenderProfile?.ClipboardPolicy.PasteSafety
            ?? TerminalClipboardPolicy.Default.PasteSafety;
        if (TerminalPasteSafety.RequiresConfirmation(pasteInput, policy, bracketed))
        {
            return TerminalPasteResult.ConfirmationRequired(bracketed);
        }

        var safeText = PreparePasteText(pasteInput.Text, bracketed);
        var text = bracketed
            ? $"\u001b[200~{safeText}\u001b[201~"
            : safeText;
        await QueueInputAsync(text, cancellationToken).ConfigureAwait(false);
        return TerminalPasteResult.Completed(bracketed);
    }

    private static string PreparePasteText(string text, bool bracketed)
    {
        var characters = text.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (IsUnsafePasteControl(character))
            {
                characters[index] = ' ';
            }
            else if (!bracketed && character == '\n')
            {
                characters[index] = '\r';
            }
        }

        return new string(characters);
    }

    private static bool IsUnsafePasteControl(char character) => character is
        '\u0000' // NUL
        or '\u0003' // VINTR
        or '\u0004' // EOT
        or '\u0005' // ENQ
        or '\u0008' // BS
        or '\u000F' // VDISCARD
        or '\u0011' // VSTART
        or '\u0012' // VREPRINT
        or '\u0013' // VSTOP
        or '\u0015' // VKILL
        or '\u0016' // VLNEXT
        or '\u0017' // VWERASE
        or '\u001A' // VSUSP
        or '\u001B' // ESC
        or '\u001C' // VQUIT
        or '\u007F'; // DEL

    public ValueTask<TerminalScreenSnapshot> ReadScreenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var buffer = _terminal.Buffer;
            var capturedRowCount = Math.Min(
                _terminal.Rows,
                Math.Max(1, MaximumCapturedCells / Math.Max(1, _terminal.Cols)));
            var rows = new List<TerminalScreenRow>(capturedRowCount);
            var text = new StringBuilder(Math.Min(
                MaximumPlainTextCharacters,
                capturedRowCount * Math.Min(_terminal.Cols, 256)));
            var truncated = capturedRowCount < _terminal.Rows;

            for (var rowIndex = 0; rowIndex < capturedRowCount; rowIndex++)
            {
                var line = buffer.Lines[buffer.YDisp + rowIndex];
                var cells = new List<TerminalScreenCell>(_terminal.Cols);
                if (line is not null)
                {
                    for (var column = 0; column < _terminal.Cols; column++)
                    {
                        cells.Add(MapCell(line, column, rowIndex));
                    }

                    var lineText = line.TranslateToString(trimRight: true);
                    if (text.Length + lineText.Length + 1 > MaximumPlainTextCharacters)
                    {
                        var remaining = Math.Max(0, MaximumPlainTextCharacters - text.Length);
                        text.Append(lineText.AsSpan(0, Math.Min(remaining, lineText.Length)));
                        truncated = true;
                    }
                    else
                    {
                        text.Append(lineText);
                    }
                }

                var nextLineIndex = buffer.YDisp + rowIndex + 1;
                var wrapsIntoNextLine = nextLineIndex < buffer.Lines.Length
                    && buffer.Lines[nextLineIndex]?.IsWrapped == true;
                rows.Add(new TerminalScreenRow(rowIndex, cells, wrapsIntoNextLine));
                if (rowIndex + 1 < capturedRowCount && text.Length < MaximumPlainTextCharacters)
                {
                    text.Append('\n');
                }
            }

            var cursorAbsoluteRow = buffer.YBase + buffer.Y;
            var cursorViewportRow = cursorAbsoluteRow - buffer.YDisp;
            var cursorVisible = cursorViewportRow >= 0 && cursorViewportRow < _terminal.Rows;
            var cursorRow = Math.Clamp(cursorViewportRow, 0, _terminal.Rows - 1);
            var cursorColumn = Math.Clamp(buffer.X, 0, _terminal.Cols - 1);
            return ValueTask.FromResult(new TerminalScreenSnapshot(
                text.ToString().TrimEnd('\n'),
                cursorRow,
                cursorColumn,
                _terminal.Rows,
                _terminal.Cols,
                _terminal.IsAlternateBufferActive,
                _terminal.CurrentDirectory ?? _launch.WorkingDirectory,
                DateTimeOffset.UtcNow,
                truncated,
                rows,
                _terminal.BracketedPasteMode,
                _terminal.MouseTrackingMode != MouseTrackingMode.None,
                _contentRevision,
                string.IsNullOrEmpty(_terminal.Title) ? null : _terminal.Title,
                cursorVisible,
                Math.Max(0, buffer.YDisp - ClearedScrollbackFloorUnsafe(buffer)),
                Math.Max(0, buffer.YBase - buffer.YDisp),
                BuildVisibleCommandBoundariesUnsafe(buffer.YDisp, capturedRowCount)));
        }
    }

    public ValueTask<TerminalWaitOutcome> WaitForTextAsync(
        TerminalWaitForTextInput input,
        CancellationToken cancellationToken) =>
        TerminalAutomationWaiter.WaitForTextAsync(
            input,
            ReadScreenAsync,
            SnapshotAsync,
            cancellationToken);

    public ValueTask<TerminalWaitOutcome> WaitForChangeAsync(
        TerminalWaitForChangeInput input,
        CancellationToken cancellationToken) =>
        TerminalAutomationWaiter.WaitForChangeAsync(
            input,
            ReadScreenAsync,
            SnapshotAsync,
            cancellationToken);

    public ValueTask<TerminalWaitOutcome> WaitForStableAsync(
        TerminalWaitForStableInput input,
        CancellationToken cancellationToken) =>
        TerminalAutomationWaiter.WaitForStableAsync(
            input,
            ReadScreenAsync,
            SnapshotAsync,
            cancellationToken);

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_failure is not null)
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Failed,
                    SessionHealth.Failed,
                    false,
                    _failure.Message,
                    _failure));
            }

            if (_closed)
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    "The portable terminal session is closed."));
            }

            if (_processExited)
            {
                var exitDetail = _exitCode is { } exitCode
                    ? $" with code {exitCode}"
                    : string.Empty;
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    $"The terminal process exited{exitDetail}."));
            }

            return ValueTask.FromResult(new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                true,
                _rendererAttached
                    ? "XTerm.NET · portable PTY · live"
                    : "Portable PTY active; renderer detached."));
        }
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sessionEvent in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (sessionEvent.Sequence > afterSequence)
            {
                yield return sessionEvent;
            }
        }
    }

    public async ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed)
            {
                return PanelCloseOutcome.AlreadyClosed;
            }

            if (!_processExited && mode == PanelCloseMode.Graceful)
            {
                return PanelCloseOutcome.ConfirmationRequired;
            }
        }

        await StopAsync(force: mode == PanelCloseMode.Force).ConfigureAwait(false);
        return mode == PanelCloseMode.Force
            ? PanelCloseOutcome.ForceTerminated
            : PanelCloseOutcome.GracefullyClosed;
    }

    public async ValueTask DisposeAsync() => await StopAsync(force: true).ConfigureAwait(false);

    private async ValueTask QueueInputAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_processExited)
            {
                throw new InvalidOperationException("The terminal process has exited.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException(
                    $"The terminal session has failed: {_failure.StableCode}.");
            }
        }

        var input = new QueuedTerminalInput(text, cancellationToken);
        try
        {
            await _writes.Writer.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            await input.Completion.ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new InvalidOperationException(
                "The terminal input queue is no longer accepting input.",
                exception);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await _pty.Reader.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    MarkProcessExited(exitCode: null);
                    break;
                }

                var characterCount = decoder.GetChars(
                    bytes.AsSpan(0, count),
                    characters.AsSpan(),
                    flush: false);
                if (characterCount == 0)
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_closed)
                    {
                        return;
                    }

                    _oscParser.Process(
                        characters.AsSpan(0, characterCount),
                        WriteTerminalOutputUnsafe,
                        ObserveOscUnsafe,
                        HandleClipboardOscUnsafe);
                    _contentRevision++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_closed)
        {
        }
        catch (IOException exception)
        {
            if (!TryMarkKnownProcessExit())
            {
                Fail("portable_terminal_read_failed", exception);
            }
        }
        catch (Exception exception)
        {
            Fail("portable_terminal_read_failed", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(characters);
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            await foreach (var input in _writes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (input.CancellationToken.IsCancellationRequested)
                {
                    input.Cancel();
                    continue;
                }

                var rejection = GetInputRejection();
                if (rejection is not null)
                {
                    input.Fail(rejection);
                    continue;
                }

                var committed = false;
                try
                {
                    using var deliveryCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            input.CancellationToken);
                    var bytes = Encoding.UTF8.GetBytes(input.Text);
                    await _pty.Writer
                        .WriteAsync(bytes, deliveryCancellation.Token)
                        .ConfigureAwait(false);
                    committed = true;

                    // A successful PTY WriteAsync is the irreversible input commit point.
                    // Caller authority no longer applies after it: reporting cancellation
                    // or a retryable flush failure could duplicate a command that already
                    // reached the child process. Flush still gates the normal receipt, but
                    // any post-commit failure completes this input before failing the
                    // session (or finishing shutdown) and rejecting input that has not
                    // yet been committed.
                    try
                    {
                        await _pty.Writer
                            .FlushAsync(cancellationToken)
                            .ConfigureAwait(false);
                        input.Complete();
                    }
                    catch (Exception)
                    {
                        input.Complete();
                        throw;
                    }
                }
                catch (OperationCanceledException) when (
                    !committed
                    && input.CancellationToken.IsCancellationRequested)
                {
                    input.Cancel();
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    input.Cancel(cancellationToken);
                    throw;
                }
                catch (Exception exception)
                {
                    input.Fail(exception);
                    throw;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            completionError = exception;
        }
        catch (ObjectDisposedException exception) when (_closed)
        {
            completionError = exception;
        }
        catch (IOException exception)
        {
            completionError = exception;
            if (!TryMarkKnownProcessExit())
            {
                Fail("portable_terminal_write_failed", exception);
            }
        }
        catch (Exception exception)
        {
            completionError = exception;
            Fail("portable_terminal_write_failed", exception);
        }
        finally
        {
            _writes.Writer.TryComplete(completionError);
            while (_writes.Reader.TryRead(out var input))
            {
                if (input.CancellationToken.IsCancellationRequested)
                {
                    input.Cancel();
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    input.Cancel(cancellationToken);
                }
                else
                {
                    input.Fail(completionError ?? new InvalidOperationException(
                        "The terminal input queue stopped before delivery."));
                }
            }
        }
    }

    private Exception? GetInputRejection()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return new ObjectDisposedException(nameof(PortableTerminalSession));
            }

            if (_processExited)
            {
                return new InvalidOperationException("The terminal process has exited.");
            }

            return _failure is null
                ? null
                : new InvalidOperationException(
                    $"The terminal session has failed: {_failure.StableCode}.");
        }
    }

    private async ValueTask StopAsync(bool force)
    {
        Task? existingStop = null;
        lock (_gate)
        {
            if (_closed)
            {
                existingStop = _stopped.Task;
            }
            else
            {
                _closed = true;
                _rendererAttached = false;
            }
        }

        if (existingStop is not null)
        {
            await existingStop.ConfigureAwait(false);
            return;
        }

        try
        {
            if (force && !_processExited)
            {
                try
                {
                    _pty.Kill();
                }
                catch (InvalidOperationException)
                {
                    // The process can exit between the state check and the kill request.
                }
            }

            _lifetime.Cancel();
            _writes.Writer.TryComplete();
            _pty.ProcessExited -= OnProcessExited;
            _terminal.DataReceived -= OnTerminalDataReceived;
            _pty.Dispose();
            try
            {
                await Task.WhenAll(_readerTask, _writerTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            lock (_gate)
            {
                PublishUnsafe(SessionLifecycle.Closed, SessionHealth.Ended, "Portable terminal session closed.");
                _events.Writer.TryComplete();
            }

            _lifetime.Dispose();
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }

    private void ResizeUnsafe(ViewportDescriptor viewport)
    {
        var profile = _launch.RenderProfile;
        var fontSize = profile?.FontSize ?? 13;
        var lineHeight = profile?.LineHeight ?? 1;
        var columns = viewport.Columns
            ?? (viewport.LogicalWidth > 0
                ? (int)Math.Floor(viewport.LogicalWidth / Math.Max(1, fontSize * 0.62))
                : _terminal.Cols);
        var rows = viewport.Rows
            ?? (viewport.LogicalHeight > 0
                ? (int)Math.Floor(viewport.LogicalHeight / Math.Max(1, fontSize * lineHeight * 1.2))
                : _terminal.Rows);
        columns = Math.Clamp(columns, 2, 1_000);
        rows = Math.Clamp(rows, 1, 1_000);
        if (columns == _terminal.Cols && rows == _terminal.Rows)
        {
            return;
        }

        _terminal.Resize(columns, rows);
        _pty.Resize(columns, rows);
        _contentRevision++;
    }

    private void PrepareForTerminalInputUnsafe()
    {
        var viewportChanged = !_terminal.Buffer.IsAtBottom;
        var selectionChanged = _selectionStart is not null || _terminal.Selection.HasSelection;
        if (viewportChanged)
        {
            _terminal.ScrollToBottom();
        }

        if (selectionChanged)
        {
            _terminal.Selection.ClearSelection();
            _selectionStart = null;
            _selectionEnd = null;
        }

        if (viewportChanged || selectionChanged)
        {
            _contentRevision++;
        }
    }

    private void ClearSelectionUnsafe()
    {
        if (_selectionStart is null && !_terminal.Selection.HasSelection)
        {
            return;
        }

        _terminal.Selection.ClearSelection();
        _selectionStart = null;
        _selectionEnd = null;
        _contentRevision++;
    }

    private List<TerminalFindMatch> FindMatchesUnsafe(string query, out bool truncated)
    {
        var matches = new List<TerminalFindMatch>();
        var buffer = _terminal.Buffer;
        var text = new StringBuilder();
        var cells = new List<TerminalFindCell>();
        var scannedCharacters = 0;
        truncated = false;

        for (var row = ClearedScrollbackFloorUnsafe(buffer); row < buffer.Lines.Length; row++)
        {
            if (buffer.Lines[row] is { } line)
            {
                for (var column = 0; column < _terminal.Cols; column++)
                {
                    var cell = line[column];
                    if (cell.Width == 0)
                    {
                        continue;
                    }

                    var content = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
                    if (scannedCharacters + content.Length > MaximumFindCharacters)
                    {
                        truncated = true;
                        break;
                    }

                    cells.Add(new TerminalFindCell(
                        text.Length,
                        content.Length,
                        row,
                        column,
                        Math.Max(1, cell.Width)));
                    text.Append(content);
                    scannedCharacters += content.Length;
                }
            }

            var nextLineContinues = row + 1 < buffer.Lines.Length
                && buffer.Lines[row + 1]?.IsWrapped == true;
            if (!nextLineContinues || truncated)
            {
                AppendFindMatches(query, text, cells, matches);
                text.Clear();
                cells.Clear();
            }

            if (truncated || matches.Count >= MaximumFindMatches)
            {
                truncated = true;
                break;
            }
        }

        return matches;
    }

    private static void AppendFindMatches(
        string query,
        StringBuilder text,
        IReadOnlyList<TerminalFindCell> cells,
        List<TerminalFindMatch> matches)
    {
        if (text.Length == 0 || cells.Count == 0)
        {
            return;
        }

        var searchable = text.ToString().TrimEnd();
        var searchFrom = 0;
        while (searchFrom <= searchable.Length - query.Length
            && matches.Count < MaximumFindMatches)
        {
            var matchIndex = searchable.IndexOf(
                query,
                searchFrom,
                StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return;
            }

            var start = FindSearchCell(cells, matchIndex);
            var end = FindSearchCell(cells, matchIndex + query.Length - 1);
            if (start is not null && end is not null)
            {
                matches.Add(new TerminalFindMatch(start, end));
            }

            searchFrom = matchIndex + Math.Max(1, query.Length);
        }
    }

    private static TerminalFindCell? FindSearchCell(
        IReadOnlyList<TerminalFindCell> cells,
        int characterIndex)
    {
        foreach (var cell in cells)
        {
            if (characterIndex >= cell.CharacterIndex
                && characterIndex < cell.CharacterIndex + cell.CharacterLength)
            {
                return cell;
            }
        }

        return null;
    }

    private void SelectFindMatchUnsafe(TerminalFindMatch match)
    {
        var buffer = _terminal.Buffer;
        var viewportTop = buffer.YDisp;
        var matchOutsideViewport = match.Start.Row < viewportTop
            || match.End.Row >= viewportTop + _terminal.Rows;
        if (matchOutsideViewport)
        {
            buffer.ViewportY = Math.Clamp(match.Start.Row, 0, buffer.YBase);
            viewportTop = buffer.YDisp;
        }

        var startRow = match.Start.Row - viewportTop;
        var endRow = match.End.Row - viewportTop;
        var endColumn = Math.Min(
            _terminal.Cols - 1,
            match.End.Column + match.End.Width - 1);
        // XTerm.NET converts these viewport-relative values to absolute buffer rows
        // without clamping. Keeping an off-viewport end row preserves a long match
        // while the viewport deterministically shows its beginning.
        _terminal.Selection.StartSelection(match.Start.Column, startRow);
        _terminal.Selection.UpdateSelection(endColumn, endRow);
        _terminal.Selection.EndSelection();
        _selectionStart = new SelectionEndpoint(
            buffer,
            buffer.Lines[match.Start.Row]
                ?? throw new InvalidOperationException("The terminal find start row is unavailable."),
            match.Start.Column);
        _selectionEnd = new SelectionEndpoint(
            buffer,
            buffer.Lines[match.End.Row]
                ?? throw new InvalidOperationException("The terminal find end row is unavailable."),
            endColumn);
        _contentRevision++;
    }

    private int NormalizeWideCellColumnUnsafe(int column, int row)
    {
        var lineIndex = _terminal.Buffer.YDisp + row;
        if (lineIndex < 0 || lineIndex >= _terminal.Buffer.Lines.Length)
        {
            return column;
        }

        var line = _terminal.Buffer.Lines[lineIndex];
        while (line is not null && column > 0 && line[column].Width == 0)
        {
            column--;
        }

        return column;
    }

    private SelectionEndpoint CreateSelectionEndpointUnsafe(int column, int row)
    {
        var buffer = _terminal.Buffer;
        var lineIndex = Math.Clamp(buffer.YDisp + row, 0, buffer.Lines.Length - 1);
        var line = buffer.Lines[lineIndex]
            ?? throw new InvalidOperationException("The terminal selection row is unavailable.");
        return new SelectionEndpoint(buffer, line, column);
    }

    private string? BuildSelectionTextUnsafe(SelectionEndpoint first, SelectionEndpoint second)
    {
        if (!ReferenceEquals(first.Buffer, second.Buffer)
            || !ReferenceEquals(first.Buffer, _terminal.Buffer))
        {
            return null;
        }

        var buffer = first.Buffer;
        var firstRow = FindLineIndex(buffer, first.Line);
        var secondRow = FindLineIndex(buffer, second.Line);
        if (firstRow < 0 || secondRow < 0)
        {
            return null;
        }

        var start = (Row: firstRow, Column: first.Column);
        var end = (Row: secondRow, Column: second.Column);
        if (start.Row > end.Row || (start.Row == end.Row && start.Column > end.Column))
        {
            (start, end) = (end, start);
        }

        var text = new StringBuilder();
        for (var row = start.Row; row <= end.Row; row++)
        {
            if (buffer.Lines[row] is not { } line)
            {
                continue;
            }

            var startColumn = row == start.Row ? start.Column : 0;
            var endColumn = row == end.Row ? end.Column : _terminal.Cols - 1;
            text.Append(line.TranslateToString(
                trimRight: false,
                startCol: Math.Clamp(startColumn, 0, _terminal.Cols - 1),
                endCol: Math.Clamp(endColumn, 0, _terminal.Cols - 1) + 1));

            if (row < end.Row && buffer.Lines[row + 1]?.IsWrapped != true)
            {
                text.Append('\n');
            }

            if (text.Length > TerminalSelectionText.MaximumCharacters)
            {
                break;
            }
        }

        return text.ToString();
    }

    private static int FindLineIndex(TerminalBuffer buffer, BufferLine line)
    {
        for (var index = 0; index < buffer.Lines.Length; index++)
        {
            if (ReferenceEquals(buffer.Lines[index], line))
            {
                return index;
            }
        }

        return -1;
    }

    private int ClearedScrollbackFloorUnsafe(TerminalBuffer buffer)
    {
        if (!_clearedScrollbackBoundaries.TryGetValue(buffer, out var boundary))
        {
            return 0;
        }

        var row = FindLineIndex(buffer, boundary);
        if (row >= 0)
        {
            return row;
        }

        _clearedScrollbackBoundaries.Remove(buffer);
        return 0;
    }

    private void WriteTerminalOutputUnsafe(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var activeHyperlink = _terminal.CurrentHyperlink;
            var beforeBuffer = _terminal.Buffer;
            var beforeAbsoluteRow = beforeBuffer.YBase + beforeBuffer.Y;
            var beforeColumn = beforeBuffer.X;
            var candidates = CaptureHyperlinkCandidatesUnsafe(
                beforeBuffer,
                beforeAbsoluteRow,
                beforeColumn);

            _terminal.Write(rune.ToString());

            TrackHyperlinkChangesUnsafe(
                candidates,
                activeHyperlink,
                rune,
                beforeBuffer,
                beforeAbsoluteRow,
                beforeColumn);
        }
    }

    private List<TrackedCell> CaptureHyperlinkCandidatesUnsafe(
        TerminalBuffer buffer,
        int absoluteRow,
        int column)
    {
        var candidates = new List<TrackedCell>(24);
        Span<int> columns =
        [
            0,
            1,
            _terminal.Cols - 2,
            _terminal.Cols - 1,
            column - 2,
            column - 1,
            column,
            column + 1,
            column + 2,
        ];
        for (var row = absoluteRow - 1; row <= absoluteRow + 1; row++)
        {
            if (row < 0 || row >= buffer.Lines.Length || buffer.Lines[row] is not { } line)
            {
                continue;
            }

            foreach (var candidateColumn in columns)
            {
                if (candidateColumn < 0
                    || candidateColumn >= _terminal.Cols
                    || candidates.Any(candidate =>
                        ReferenceEquals(candidate.Line, line)
                        && candidate.Column == candidateColumn))
                {
                    continue;
                }

                candidates.Add(new TrackedCell(line, candidateColumn, line[candidateColumn]));
            }
        }

        return candidates;
    }

    private void TrackHyperlinkChangesUnsafe(
        IReadOnlyList<TrackedCell> candidates,
        string? activeHyperlink,
        Rune rune,
        TerminalBuffer beforeBuffer,
        int beforeAbsoluteRow,
        int beforeColumn)
    {
        var changed = new List<(BufferLine Line, int Column, BufferCell Cell)>();
        foreach (var candidate in candidates)
        {
            var current = candidate.Line[candidate.Column];
            if (current == candidate.Cell)
            {
                continue;
            }

            RemoveHyperlinkUnsafe(candidate.Line, candidate.Column);
            changed.Add((candidate.Line, candidate.Column, current));
        }

        if (string.IsNullOrEmpty(activeHyperlink))
        {
            return;
        }

        var runeText = rune.ToString();
        foreach (var candidate in changed)
        {
            if (!candidate.Cell.Content.Contains(runeText, StringComparison.Ordinal))
            {
                continue;
            }

            SetHyperlinkUnsafe(candidate.Line, candidate.Column, candidate.Cell, activeHyperlink);
            if (candidate.Cell.Width == 2 && candidate.Column + 1 < _terminal.Cols)
            {
                SetHyperlinkUnsafe(
                    candidate.Line,
                    candidate.Column + 1,
                    candidate.Line[candidate.Column + 1],
                    activeHyperlink);
            }
        }

        if (changed.Count != 0
            || !ReferenceEquals(beforeBuffer, _terminal.Buffer)
            || beforeColumn < _terminal.Cols)
        {
            return;
        }

        // A printable rune at the pending-wrap column can create a new circular-buffer line,
        // which did not exist in the pre-write candidate set.
        var buffer = _terminal.Buffer;
        var absoluteRow = buffer.YBase + buffer.Y;
        if (absoluteRow <= beforeAbsoluteRow || buffer.X <= 0 || absoluteRow >= buffer.Lines.Length)
        {
            return;
        }

        var line = buffer.Lines[absoluteRow];
        var writtenColumn = Math.Clamp(buffer.X - 1, 0, _terminal.Cols - 1);
        while (line is not null && writtenColumn > 0 && line[writtenColumn].Width == 0)
        {
            writtenColumn--;
        }

        if (line is null || !line[writtenColumn].Content.Contains(runeText, StringComparison.Ordinal))
        {
            return;
        }

        SetHyperlinkUnsafe(line, writtenColumn, line[writtenColumn], activeHyperlink);
        if (line[writtenColumn].Width == 2 && writtenColumn + 1 < _terminal.Cols)
        {
            SetHyperlinkUnsafe(line, writtenColumn + 1, line[writtenColumn + 1], activeHyperlink);
        }
    }

    private void SetHyperlinkUnsafe(
        BufferLine line,
        int column,
        BufferCell cell,
        string hyperlink)
    {
        if (!_hyperlinks.TryGetValue(line, out var columns))
        {
            columns = [];
            _hyperlinks.Add(line, columns);
        }

        columns[column] = new HyperlinkCellStamp(hyperlink, cell);
    }

    private void RemoveHyperlinkUnsafe(BufferLine line, int column)
    {
        if (!_hyperlinks.TryGetValue(line, out var columns))
        {
            return;
        }

        columns.Remove(column);
        if (columns.Count == 0)
        {
            _hyperlinks.Remove(line);
        }
    }

    private string? GetHyperlinkUnsafe(BufferLine line, int column, BufferCell cell)
    {
        if (!_hyperlinks.TryGetValue(line, out var columns)
            || !columns.TryGetValue(column, out var stamp))
        {
            return null;
        }

        if (stamp.Cell == cell)
        {
            return stamp.Hyperlink;
        }

        RemoveHyperlinkUnsafe(line, column);
        return null;
    }

    private void ObserveOscUnsafe(string payload)
    {
        if (_launch.RenderProfile?.ShellIntegration == TerminalShellIntegrationMode.Disabled)
        {
            return;
        }

        var parts = payload.Split(';');
        if (parts.Length < 2 || !string.Equals(parts[0], "133", StringComparison.Ordinal))
        {
            return;
        }

        TerminalCommandBoundaryKind? kind = parts[1] switch
        {
            "A" => TerminalCommandBoundaryKind.PromptStarted,
            "B" => TerminalCommandBoundaryKind.CommandInputStarted,
            "C" => TerminalCommandBoundaryKind.CommandExecuted,
            "D" => TerminalCommandBoundaryKind.CommandFinished,
            _ => null,
        };
        if (kind is null)
        {
            return;
        }

        int? exitCode = null;
        if (kind == TerminalCommandBoundaryKind.CommandFinished
            && parts.Length >= 3
            && int.TryParse(
                parts[2],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedExitCode))
        {
            exitCode = parsedExitCode;
        }

        var buffer = _terminal.Buffer;
        var absoluteRow = buffer.YBase + buffer.Y;
        if (absoluteRow < 0
            || absoluteRow >= buffer.Lines.Length
            || buffer.Lines[absoluteRow] is not { } line)
        {
            return;
        }

        _commandBoundaries.Add(new CommandBoundaryMarker(
            ++_commandBoundarySequence,
            kind.Value,
            buffer,
            line,
            Math.Clamp(buffer.X, 0, _terminal.Cols - 1),
            exitCode));
        if (_commandBoundaries.Count > MaximumCommandBoundaries)
        {
            _commandBoundaries.RemoveAt(0);
        }
    }

    private void HandleClipboardOscUnsafe(string payload)
    {
        var parts = payload.Split(';', 3);
        if (parts.Length < 3 || !string.Equals(parts[0], "52", StringComparison.Ordinal))
        {
            return;
        }

        var policy = _launch.RenderProfile?.ClipboardPolicy ?? TerminalClipboardPolicy.Default;
        if (!string.Equals(parts[2], "?", StringComparison.Ordinal))
        {
            // XTerm.NET exposes no safe system-clipboard broker. Even when writes are
            // allowed by profile, OSC 52 sets therefore fail closed instead of silently
            // crossing the engine/presentation boundary.
            _ = policy.WriteAccess;
            return;
        }

        // Clipboard reads also fail closed without a broker. An empty OSC 52 response is
        // deterministic and prevents the requesting process from waiting indefinitely.
        _ = policy.ReadAccess;
        var target = parts[1].Length is > 0 and <= 16
            && parts[1].All(character => char.IsAsciiLetterOrDigit(character))
                ? parts[1]
                : "c";
        if (!TryQueueProtocolInput($"\u001b]52;{target};\u0007"))
        {
            Fail(
                "portable_terminal_input_backpressure",
                new InvalidOperationException(
                    "The bounded terminal input queue could not accept a clipboard-denial response."));
        }
    }

    private IReadOnlyList<TerminalCommandBoundary> BuildVisibleCommandBoundariesUnsafe(
        int viewportTop,
        int rowCount)
    {
        var buffer = _terminal.Buffer;
        var activeLines = new HashSet<BufferLine>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < buffer.Lines.Length; index++)
        {
            if (buffer.Lines[index] is { } line)
            {
                activeLines.Add(line);
            }
        }

        _commandBoundaries.RemoveAll(marker =>
            ReferenceEquals(marker.Buffer, buffer) && !activeLines.Contains(marker.Line));

        var visibleRows = new Dictionary<BufferLine, int>(ReferenceEqualityComparer.Instance);
        for (var row = 0; row < rowCount; row++)
        {
            var lineIndex = viewportTop + row;
            if (lineIndex < buffer.Lines.Length && buffer.Lines[lineIndex] is { } line)
            {
                visibleRows[line] = row;
            }
        }

        return _commandBoundaries
            .Where(marker =>
                ReferenceEquals(marker.Buffer, buffer)
                && visibleRows.ContainsKey(marker.Line))
            .Select(marker => new TerminalCommandBoundary(
                marker.Sequence,
                marker.Kind,
                visibleRows[marker.Line],
                marker.Column,
                marker.ExitCode))
            .ToArray();
    }

    private void OnTerminalDataReceived(object? sender, XTerm.Events.TerminalEvents.DataEventArgs eventArgs)
    {
        if (!TryQueueProtocolInput(eventArgs.Data))
        {
            Fail(
                "portable_terminal_input_backpressure",
                new InvalidOperationException("The bounded terminal input queue could not accept a protocol response."));
        }
    }

    private bool TryQueueProtocolInput(string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        var input = new QueuedTerminalInput(text, CancellationToken.None);
        if (!_writes.Writer.TryWrite(input))
        {
            return false;
        }

        _ = ObserveProtocolInputAsync(input.Completion);
        return true;
    }

    private static async Task ObserveProtocolInputAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The writer loop records delivery failures on the session. This observer
            // consumes the detached acknowledgement so it cannot become unobserved.
        }
    }

    private void OnProcessExited(object? sender, PortablePtyExit eventArgs)
        => MarkProcessExited(eventArgs.ExitCode);

    private bool TryMarkKnownProcessExit()
    {
        if (!_pty.TryGetExitCode(out var exitCode))
        {
            return false;
        }

        MarkProcessExited(exitCode);
        return true;
    }

    private void MarkProcessExited(int? exitCode)
    {
        lock (_gate)
        {
            if (_closed || _processExited)
            {
                return;
            }

            _processExited = true;
            _exitCode = exitCode;
            _writes.Writer.TryComplete();
            var detail = exitCode is { } knownExitCode
                ? $"Terminal process exited with code {knownExitCode}."
                : "Terminal process exited.";
            PublishUnsafe(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                detail);
        }
    }

    private void Fail(string stableCode, Exception exception)
    {
        lock (_gate)
        {
            if (_closed || _failure is not null)
            {
                return;
            }

            _failure = new SessionFailure(stableCode, exception.Message, true);
            _writes.Writer.TryComplete(exception);
            PublishUnsafe(SessionLifecycle.Failed, SessionHealth.Failed, exception.Message);
        }
    }

    private void PublishUnsafe(SessionLifecycle lifecycle, SessionHealth health, string detail)
    {
        _sequence++;
        _events.Writer.TryWrite(new PanelSessionEvent(
            _sequence,
            lifecycle,
            health,
            DateTimeOffset.UtcNow,
            detail));
    }

    private TerminalScreenCell MapCell(BufferLine line, int column, int viewportRow)
    {
        var cell = line[column];
        var selected = _terminal.Selection.IsCellSelected(column, viewportRow);
        if (cell.Width == 2 && column + 1 < _terminal.Cols)
        {
            selected |= _terminal.Selection.IsCellSelected(column + 1, viewportRow);
        }

        return new TerminalScreenCell(
            cell.Content ?? string.Empty,
            Math.Clamp(cell.Width, 0, 2),
            MapColor(cell.Attributes.GetFgColorMode(), cell.Attributes.GetFgColor(), defaultMarker: 256),
            MapColor(cell.Attributes.GetBgColorMode(), cell.Attributes.GetBgColor(), defaultMarker: 257),
            MapStyle(cell.Attributes),
            GetHyperlinkUnsafe(line, column, cell),
            selected);
    }

    private static TerminalCellColor MapColor(int mode, int value, int defaultMarker)
    {
        if (value == defaultMarker)
        {
            return TerminalCellColor.Default;
        }

        return mode switch
        {
            // XTerm.NET 1.0.15 emits both ANSI and 256-color palette values in
            // mode 0, using 256/257 as the default foreground/background markers.
            // Packed true-color values are mode 1; mode 2 is accepted for forward
            // compatibility with the public package documentation.
            0 => new TerminalCellColor(TerminalColorMode.Indexed, Math.Clamp(value, 0, 255)),
            1 or 2 => new TerminalCellColor(TerminalColorMode.Rgb, Math.Clamp(value, 0, 0xFFFFFF)),
            _ => TerminalCellColor.Default,
        };
    }

    private static TerminalCellStyle MapStyle(AttributeData attributes)
    {
        var style = TerminalCellStyle.None;
        style |= attributes.IsBold() ? TerminalCellStyle.Bold : TerminalCellStyle.None;
        style |= attributes.IsDim() ? TerminalCellStyle.Dim : TerminalCellStyle.None;
        style |= attributes.IsItalic() ? TerminalCellStyle.Italic : TerminalCellStyle.None;
        style |= attributes.IsUnderline() ? TerminalCellStyle.Underline : TerminalCellStyle.None;
        style |= attributes.IsBlink() ? TerminalCellStyle.Blink : TerminalCellStyle.None;
        style |= attributes.IsInverse() ? TerminalCellStyle.Inverse : TerminalCellStyle.None;
        style |= attributes.IsInvisible() ? TerminalCellStyle.Invisible : TerminalCellStyle.None;
        style |= attributes.IsStrikethrough() ? TerminalCellStyle.Strikethrough : TerminalCellStyle.None;
        style |= attributes.IsOverline() ? TerminalCellStyle.Overline : TerminalCellStyle.None;
        return style;
    }

    private static XTerm.Common.CursorStyle MapCursorStyle(TerminalCursorStyle style) => style switch
    {
        TerminalCursorStyle.Block => XTerm.Common.CursorStyle.Block,
        TerminalCursorStyle.Bar => XTerm.Common.CursorStyle.Bar,
        TerminalCursorStyle.Underline => XTerm.Common.CursorStyle.Underline,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown cursor style."),
    };

    private static XKey MapKey(TerminalKey key) => key switch
    {
        TerminalKey.Enter => XKey.Enter,
        TerminalKey.Tab => XKey.Tab,
        TerminalKey.Backspace => XKey.Backspace,
        TerminalKey.Escape => XKey.Escape,
        TerminalKey.Space => XKey.Space,
        TerminalKey.Up => XKey.UpArrow,
        TerminalKey.Down => XKey.DownArrow,
        TerminalKey.Left => XKey.LeftArrow,
        TerminalKey.Right => XKey.RightArrow,
        TerminalKey.Home => XKey.Home,
        TerminalKey.End => XKey.End,
        TerminalKey.PageUp => XKey.PageUp,
        TerminalKey.PageDown => XKey.PageDown,
        TerminalKey.Insert => XKey.Insert,
        TerminalKey.Delete => XKey.Delete,
        TerminalKey.F1 => XKey.F1,
        TerminalKey.F2 => XKey.F2,
        TerminalKey.F3 => XKey.F3,
        TerminalKey.F4 => XKey.F4,
        TerminalKey.F5 => XKey.F5,
        TerminalKey.F6 => XKey.F6,
        TerminalKey.F7 => XKey.F7,
        TerminalKey.F8 => XKey.F8,
        TerminalKey.F9 => XKey.F9,
        TerminalKey.F10 => XKey.F10,
        TerminalKey.F11 => XKey.F11,
        TerminalKey.F12 => XKey.F12,
        TerminalKey.F13 => XKey.F13,
        TerminalKey.F14 => XKey.F14,
        TerminalKey.F15 => XKey.F15,
        TerminalKey.F16 => XKey.F16,
        TerminalKey.F17 => XKey.F17,
        TerminalKey.F18 => XKey.F18,
        TerminalKey.F19 => XKey.F19,
        TerminalKey.F20 => XKey.F20,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown terminal key."),
    };

    private static XKeyModifiers MapModifiers(TerminalKeyModifiers modifiers)
    {
        var result = XKeyModifiers.None;
        result |= modifiers.HasFlag(TerminalKeyModifiers.Shift) ? XKeyModifiers.Shift : XKeyModifiers.None;
        result |= modifiers.HasFlag(TerminalKeyModifiers.Alt) || modifiers.HasFlag(TerminalKeyModifiers.Meta)
            ? XKeyModifiers.Alt
            : XKeyModifiers.None;
        result |= modifiers.HasFlag(TerminalKeyModifiers.Control) ? XKeyModifiers.Control : XKeyModifiers.None;
        return result;
    }

    private static XKeyModifiers MapChordModifier(
        TerminalCharacterChordModifier modifier) =>
        modifier switch
        {
            TerminalCharacterChordModifier.Control => XKeyModifiers.Control,
            TerminalCharacterChordModifier.Alt => XKeyModifiers.Alt,
            _ => throw new ArgumentOutOfRangeException(
                nameof(modifier),
                modifier,
                "Unknown terminal character chord modifier."),
        };

    private static XMouseButton MapMouseButton(TerminalMouseButton button) => button switch
    {
        TerminalMouseButton.None => XMouseButton.None,
        TerminalMouseButton.Left => XMouseButton.Left,
        TerminalMouseButton.Middle => XMouseButton.Middle,
        TerminalMouseButton.Right => XMouseButton.Right,
        TerminalMouseButton.WheelUp => XMouseButton.WheelUp,
        TerminalMouseButton.WheelDown => XMouseButton.WheelDown,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
    };

    private static XMouseEventType MapMouseKind(TerminalMouseEventKind kind) => kind switch
    {
        TerminalMouseEventKind.Down => XMouseEventType.Down,
        TerminalMouseEventKind.Up => XMouseEventType.Up,
        TerminalMouseEventKind.Move => XMouseEventType.Move,
        TerminalMouseEventKind.Drag => XMouseEventType.Drag,
        TerminalMouseEventKind.WheelUp => XMouseEventType.WheelUp,
        TerminalMouseEventKind.WheelDown => XMouseEventType.WheelDown,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mouse event kind."),
    };

    private sealed class QueuedTerminalInput
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QueuedTerminalInput(string text, CancellationToken cancellationToken)
        {
            Text = text;
            CancellationToken = cancellationToken;
        }

        public string Text { get; }

        public CancellationToken CancellationToken { get; }

        public Task Completion => _completion.Task;

        public void Complete() => _completion.TrySetResult();

        public void Cancel() => Cancel(CancellationToken);

        public void Cancel(CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                _completion.TrySetCanceled();
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private readonly record struct HyperlinkCellStamp(string Hyperlink, BufferCell Cell);

    private readonly record struct TrackedCell(BufferLine Line, int Column, BufferCell Cell);

    private sealed record SelectionEndpoint(
        TerminalBuffer Buffer,
        BufferLine Line,
        int Column);

    private sealed record TerminalFindCell(
        int CharacterIndex,
        int CharacterLength,
        int Row,
        int Column,
        int Width);

    private sealed record TerminalFindMatch(
        TerminalFindCell Start,
        TerminalFindCell End);

    private sealed record CommandBoundaryMarker(
        long Sequence,
        TerminalCommandBoundaryKind Kind,
        TerminalBuffer Buffer,
        BufferLine Line,
        int Column,
        int? ExitCode);
}
