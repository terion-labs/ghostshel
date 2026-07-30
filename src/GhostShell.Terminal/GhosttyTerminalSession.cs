using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

internal sealed class GhosttyTerminalSession : ITerminalPanelSession
{
    private readonly object _gate = new();
    private readonly TerminalLaunchRequest _launch;
    private readonly Channel<PanelSessionEvent> _events = Channel.CreateBounded<PanelSessionEvent>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    private GhosttyTerminalHandle? _terminal;
    private GhosttyNativeFocusObserver? _focusObserver;
    private GhosttyNativeHostKeyInterceptor? _hostKeyInterceptor;
    private GhosttyNativePhysicalInputGate? _physicalInputGate;
    private SessionFailure? _failure;
    private bool _rendererAttached;
    private bool _closed;
    private long _sequence;
    private long _contentRevision;
    private TerminalScreenFingerprint? _lastScreenFingerprint;

    public GhosttyTerminalSession(SessionId id, TerminalLaunchRequest launch)
    {
        Id = id;
        _launch = launch;
    }

    public SessionId Id { get; }

    public TerminalLaunchRequest Launch => _launch;

    public PanelKind Kind => PanelKind.Terminal;

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.NativeRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalWrite,
        SessionCapabilities.TerminalSendKeys,
        SessionCapabilities.TerminalSendChord,
        SessionCapabilities.TerminalEnter,
        SessionCapabilities.TerminalInterrupt,
        SessionCapabilities.TerminalWait,
        SessionCapabilities.TerminalMouse,
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
        if (!string.Equals(rendererHost.HandleDescriptor, "NSView", StringComparison.Ordinal))
        {
            return ValueTask.FromException(new PlatformNotSupportedException(
                "The libghostty macOS renderer requires an NSView host."));
        }

        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_terminal is null)
                {
                    _terminal = GhosttyNativeTerminal.Attach(rendererHost.Handle, _launch);
                }
                else
                {
                    GhosttyNativeTerminal.Reparent(_terminal, rendererHost.Handle);
                }

                ReplaceNativeInputCallbacksUnsafe(rendererHost);
                ApplyNativePresentationUnsafe(rendererHost);
                _rendererAttached = true;
                PublishUnsafe(SessionLifecycle.Active, SessionHealth.Healthy, "libghostty renderer attached.");
            }

            Resize(rendererHost.Viewport);
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure = new SessionFailure("engine_failed", exception.Message, false);
                PublishUnsafe(SessionLifecycle.Failed, SessionHealth.Failed, exception.Message);
            }

            return ValueTask.FromException(exception);
        }
    }

    public ValueTask DetachRendererAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_terminal is not null && _rendererAttached)
            {
                ClearNativeInputCallbacksUnsafe();
                GhosttyNativeTerminal.DetachView(_terminal);
                _rendererAttached = false;
                PublishUnsafe(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    "Renderer detached; terminal session remains active.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireAttachedTerminal();
            GhosttyNativeTerminal.Focus(terminal);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reconfigures the live surface. Ghostty applies a configuration to an
    /// existing surface, so typography changes without the process behind it
    /// noticing and without losing the scrollback.
    /// </summary>
    public ValueTask<bool> UpdateRenderProfileAsync(
        TerminalRenderProfileSnapshot renderProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderProfile);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_terminal is not { } terminal || terminal.IsInvalid)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(
                GhosttyNativeTerminal.UpdateRenderProfile(terminal, renderProfile));
        }
    }

    public ValueTask ResizeAsync(
        ViewportDescriptor viewport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Resize(viewport);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var epoch = CaptureInputEpoch(terminal, cancellationToken);
            EnsureInputDelivered(
                terminal,
                epoch,
                GhosttyNativeTerminal.SendText(terminal, text, epoch),
                "libghostty rejected the text input.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyStroke);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var epoch = CaptureInputEpoch(terminal, cancellationToken);
            EnsureInputDelivered(
                terminal,
                epoch,
                GhosttyNativeTerminal.SendKey(terminal, keyStroke, epoch),
                $"libghostty rejected the {keyStroke.Key} key input.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendChordAsync(
        TerminalCharacterChord chord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chord);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var epoch = CaptureInputEpoch(terminal, cancellationToken);
            EnsureInputDelivered(
                terminal,
                epoch,
                GhosttyNativeTerminal.SendChord(terminal, chord, epoch),
                $"libghostty rejected the {chord.Modifier}+{chord.Character} chord input.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var epoch = CaptureInputEpoch(terminal, cancellationToken);
            EnsureInputDelivered(
                terminal,
                epoch,
                GhosttyNativeTerminal.SendKey(
                    terminal,
                    new TerminalKeyStroke(TerminalKey.Enter),
                    epoch),
                "libghostty rejected the Enter key input.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask InterruptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var epoch = CaptureInputEpoch(terminal, cancellationToken);
            EnsureInputDelivered(
                terminal,
                epoch,
                GhosttyNativeTerminal.SendText(terminal, "\u0003", epoch),
                "libghostty rejected the interrupt input.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mouseInput);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var epoch = CaptureInputEpoch(terminal, cancellationToken);
            EnsureInputDelivered(
                terminal,
                epoch,
                GhosttyNativeTerminal.SendMouse(terminal, mouseInput, epoch),
                "libghostty rejected the mouse input because terminal mouse capture is inactive "
                    + "or the target cell is outside the current grid.",
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollInput);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new PlatformNotSupportedException(
            "Native libghostty owns scrollback interaction inside its renderer."));
    }

    public ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectionInput);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new PlatformNotSupportedException(
            "Native libghostty owns text selection inside its renderer."));
    }

    public ValueTask<TerminalSelectionText> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException<TerminalSelectionText>(new PlatformNotSupportedException(
            "Native libghostty owns clipboard copy inside its renderer."));
    }

    public ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pasteInput);
        cancellationToken.ThrowIfCancellationRequested();
        var policy = _launch.RenderProfile?.ClipboardPolicy.PasteSafety
            ?? TerminalClipboardPolicy.Default.PasteSafety;
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var state = GhosttyNativeTerminal.ReadScreenState(terminal);
            if (TerminalPasteSafety.RequiresConfirmation(
                    pasteInput,
                    policy,
                    state.IsBracketedPasteEnabled))
            {
                return ValueTask.FromResult(
                    TerminalPasteResult.ConfirmationRequired(state.IsBracketedPasteEnabled));
            }

            var epoch = CaptureInputEpoch(terminal, cancellationToken);
            EnsureInputDelivered(
                terminal,
                epoch,
                GhosttyNativeTerminal.PasteText(terminal, pasteInput.Text, epoch),
                "libghostty rejected the paste input.",
                cancellationToken);
            return ValueTask.FromResult(
                TerminalPasteResult.Completed(state.IsBracketedPasteEnabled));
        }
    }

    public ValueTask<TerminalScreenSnapshot> ReadScreenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var terminal = RequireTerminal();
            var text = GhosttyNativeTerminal.ReadScreen(terminal);
            var state = GhosttyNativeTerminal.ReadScreenState(terminal);
            var workingDirectory = GhosttyNativeTerminal.ReadWorkingDirectory(terminal)
                ?? _launch.WorkingDirectory;
            var fingerprint = new TerminalScreenFingerprint(
                text,
                state.CursorRow,
                state.CursorColumn,
                state.Rows,
                state.Columns,
                state.IsAlternateScreen,
                state.IsBracketedPasteEnabled,
                state.IsMouseTrackingEnabled,
                workingDirectory);
            if (_lastScreenFingerprint is not null
                && _lastScreenFingerprint != fingerprint)
            {
                _contentRevision++;
            }

            _lastScreenFingerprint = fingerprint;
            return ValueTask.FromResult(new TerminalScreenSnapshot(
                text,
                state.CursorRow,
                state.CursorColumn,
                state.Rows,
                state.Columns,
                state.IsAlternateScreen,
                workingDirectory,
                DateTimeOffset.UtcNow,
                IsBracketedPasteEnabled: state.IsBracketedPasteEnabled,
                IsMouseTrackingEnabled: state.IsMouseTrackingEnabled,
                ContentRevision: _contentRevision));
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
                    "The terminal session is closed."));
            }

            if (_terminal is null)
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Starting,
                    SessionHealth.Starting,
                    false,
                    "Waiting for a native renderer attachment."));
            }

            if (GhosttyNativeTerminal.HasProcessExited(_terminal))
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    "The terminal process exited."));
            }

            var hasActiveWork = NeedsCloseConfirmationUnsafe();
            return ValueTask.FromResult(new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                hasActiveWork,
                hasActiveWork
                    ? "The terminal could not be confirmed at an idle shell prompt."
                    : "libghostty 1.3.1 · Metal · live"));
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

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(PanelCloseOutcome.Cancelled);
        }

        lock (_gate)
        {
            if (_closed)
            {
                return ValueTask.FromResult(PanelCloseOutcome.AlreadyClosed);
            }

            if (_terminal is not null
                && mode == PanelCloseMode.Graceful
                && NeedsCloseConfirmationUnsafe())
            {
                return ValueTask.FromResult(PanelCloseOutcome.ConfirmationRequired);
            }

            ClearNativeInputCallbacksUnsafe();
            _terminal?.Dispose();
            _terminal = null;
            _rendererAttached = false;
            _closed = true;
            var outcome = mode == PanelCloseMode.Force
                ? PanelCloseOutcome.ForceTerminated
                : PanelCloseOutcome.GracefullyClosed;
            PublishUnsafe(SessionLifecycle.Closed, SessionHealth.Ended, "Terminal session closed.");
            _events.Writer.TryComplete();
            return ValueTask.FromResult(outcome);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return ValueTask.CompletedTask;
            }

            ClearNativeInputCallbacksUnsafe();
            _terminal?.Dispose();
            _terminal = null;
            _rendererAttached = false;
            _closed = true;
            _events.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }

    private bool NeedsCloseConfirmationUnsafe()
    {
        var terminal = _terminal;
        if (terminal is null || !GhosttyNativeTerminal.NeedsCloseConfirmation(terminal))
        {
            return false;
        }

        if (!RemoteTerminalIdleClassifier.AppliesTo(_launch))
        {
            return true;
        }

        try
        {
            return !RemoteTerminalIdleClassifier.IsAtShellPrompt(
                GhosttyNativeTerminal.ReadScreen(terminal),
                GhosttyNativeTerminal.ReadScreenState(terminal));
        }
        catch (GhosttyNativeException)
        {
            // A failed canonical read must not turn an uncertain remote session into a silent close.
            return true;
        }
    }

    private GhosttyTerminalHandle RequireTerminal() =>
        _terminal ?? throw new InvalidOperationException("The terminal renderer has not been attached.");

    private void ReplaceNativeInputCallbacksUnsafe(NativeRendererHost rendererHost)
    {
        ClearNativeInputCallbacksUnsafe();
        var terminal = RequireTerminal();
        try
        {
            _physicalInputGate = GhosttyNativePhysicalInputGate.Attach(
                terminal,
                rendererHost.PhysicalInputGate);
            _hostKeyInterceptor = GhosttyNativeHostKeyInterceptor.Attach(
                terminal,
                rendererHost.KeyInterceptor);
        }
        catch
        {
            ClearNativeInputCallbacksUnsafe();
            throw;
        }
    }

    /// <summary>
    /// Applies the presentation the host cannot express through Avalonia: the
    /// corner radius its parent card draws, and an observer for focus moving into
    /// the native view.
    /// </summary>
    private void ApplyNativePresentationUnsafe(NativeRendererHost rendererHost)
    {
        var terminal = RequireTerminal();
        var corners = rendererHost.Corners;
        _ = GhosttyNativeMethods.TerminalSetHostCornerRadiiV1(
            terminal,
            corners.TopLeft,
            corners.TopRight,
            corners.BottomRight,
            corners.BottomLeft);
        if (rendererHost.FocusObserver is { } observer)
        {
            _focusObserver = GhosttyNativeFocusObserver.Attach(terminal, observer);
        }
    }

    private void ClearNativeInputCallbacksUnsafe()
    {
        _focusObserver?.Dispose();
        _focusObserver = null;
        _hostKeyInterceptor?.Dispose();
        _hostKeyInterceptor = null;
        _physicalInputGate?.Dispose();
        _physicalInputGate = null;
    }

    private static ulong CaptureInputEpoch(
        GhosttyTerminalHandle terminal,
        CancellationToken cancellationToken)
    {
        var epoch = GhosttyNativeTerminal.ReadInputEpoch(terminal);
        cancellationToken.ThrowIfCancellationRequested();
        if (epoch == ulong.MaxValue)
        {
            throw new GhosttyNativeException(
                "The native terminal input-authority epoch is unavailable.");
        }

        return epoch;
    }

    private static void EnsureInputDelivered(
        GhosttyTerminalHandle terminal,
        ulong expectedEpoch,
        bool delivered,
        string rejectionMessage,
        CancellationToken cancellationToken)
    {
        if (delivered)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (GhosttyNativeTerminal.ReadInputEpoch(terminal) != expectedEpoch)
        {
            throw new OperationCanceledException(
                "Physical human input preempted the terminal mutation.",
                cancellationToken);
        }

        throw new GhosttyNativeException(rejectionMessage);
    }

    private GhosttyTerminalHandle RequireAttachedTerminal()
    {
        if (!_rendererAttached)
        {
            throw new InvalidOperationException("The terminal renderer is detached.");
        }

        return RequireTerminal();
    }

    private void Resize(ViewportDescriptor viewport)
    {
        if (_terminal is null || !_rendererAttached)
        {
            if (viewport.Columns is not null || viewport.Rows is not null)
            {
                throw new InvalidOperationException(
                    "An attached libghostty renderer is required for an exact terminal grid resize.");
            }

            return;
        }

        if (viewport is
            {
                Columns: { } columns,
                Rows: { } rows,
            })
        {
            if (!GhosttyNativeTerminal.ResizeGrid(
                    _terminal,
                    columns,
                    rows))
            {
                throw new GhosttyNativeException(
                    "libghostty could not apply the exact terminal cell grid.");
            }

            return;
        }

        GhosttyNativeTerminal.Resize(
            _terminal,
            viewport.LogicalWidth,
            viewport.LogicalHeight,
            viewport.RenderScale);
    }

    private void PublishUnsafe(
        SessionLifecycle lifecycle,
        SessionHealth health,
        string detail)
    {
        _sequence++;
        _events.Writer.TryWrite(new PanelSessionEvent(
            _sequence,
            lifecycle,
            health,
            DateTimeOffset.UtcNow,
            detail));
    }

    private sealed record TerminalScreenFingerprint(
        string PlainText,
        int CursorRow,
        int CursorColumn,
        int Rows,
        int Columns,
        bool IsAlternateScreen,
        bool IsBracketedPasteEnabled,
        bool IsMouseTrackingEnabled,
        string? WorkingDirectory);
}
