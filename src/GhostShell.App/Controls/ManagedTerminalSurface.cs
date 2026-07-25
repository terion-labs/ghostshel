using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using GhostShell.Application;
using GhostShell.Core;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace GhostShell.App.Controls;

/// <summary>
/// Draws the application-owned terminal cell model and translates Avalonia input into
/// typed terminal operations. The control deliberately has no dependency on a PTY or
/// terminal-emulator package; canonical state remains owned by the session host.
/// </summary>
public sealed class ManagedTerminalSurface : Control
{
    private static readonly TerminalKeymapSnapshot PlatformDefaultKeymap =
        TerminalKeymapSnapshot.FromProfile(OperatingSystem.IsMacOS()
            ? BuiltInKeymaps.MacOsTerminal
            : OperatingSystem.IsWindows()
                ? BuiltInKeymaps.WindowsTerminal
                : BuiltInKeymaps.LinuxTerminal);
    private static readonly IBrush ConfirmationBackground = Brush.Parse("#1D1D20");
    private static readonly IBrush ConfirmationBorder = Brush.Parse("#70492E");
    private static readonly IBrush ConfirmationText = Brush.Parse("#F2F1EF");
    private static readonly IBrush ConfirmationAccent = Brush.Parse("#D08A4B");

    public static readonly StyledProperty<TerminalRenderProfileSnapshot?> ProfileProperty =
        AvaloniaProperty.Register<ManagedTerminalSurface, TerminalRenderProfileSnapshot?>(
            nameof(Profile));

    public static readonly StyledProperty<TerminalKeymapSnapshot?> KeymapProperty =
        AvaloniaProperty.Register<ManagedTerminalSurface, TerminalKeymapSnapshot?>(
            nameof(Keymap));

    public static readonly DirectProperty<ManagedTerminalSurface, bool> IsPasteConfirmationVisibleProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSurface, bool>(
            nameof(IsPasteConfirmationVisible),
            control => control.IsPasteConfirmationVisible);

    public static readonly DirectProperty<ManagedTerminalSurface, bool> IsLinkConfirmationVisibleProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSurface, bool>(
            nameof(IsLinkConfirmationVisible),
            control => control.IsLinkConfirmationVisible);

    public static readonly DirectProperty<ManagedTerminalSurface, string> CommandStatusMessageProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSurface, string>(
            nameof(CommandStatusMessage),
            control => control.CommandStatusMessage);

    public static readonly DirectProperty<ManagedTerminalSurface, bool> IsFindVisibleProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSurface, bool>(
            nameof(IsFindVisible),
            control => control.IsFindVisible);

    public static readonly DirectProperty<ManagedTerminalSurface, string> FindQueryProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSurface, string>(
            nameof(FindQuery),
            control => control.FindQuery);

    public static readonly DirectProperty<ManagedTerminalSurface, string> FindStatusMessageProperty =
        AvaloniaProperty.RegisterDirect<ManagedTerminalSurface, string>(
            nameof(FindStatusMessage),
            control => control.FindStatusMessage);

    private readonly DispatcherTimer _cursorTimer;
    private readonly DispatcherTimer _keySequenceTimer;
    private readonly TerminalTextInputMethodClient _imeClient;
    private TerminalScreenSnapshot? _snapshot;
    private IManagedTerminalInputSink? _inputSink;
    private IManagedTerminalClipboard _clipboard;
    private IManagedTerminalLinkOpener _linkOpener;
    private string? _pendingPaste;
    private Uri? _pendingLink;
    private bool _isPasteConfirmationVisible;
    private bool _isLinkConfirmationVisible;
    private bool _isPasteConfirmationInFlight;
    private long _pasteGeneration;
    private bool _isInputReady = true;
    private bool _cursorVisible = true;
    private bool _blinkVisible = true;
    private bool _isAttached;
    private (int Column, int Row)? _lastPointerCell;
    private bool _isSelecting;
    private string _preeditText = string.Empty;
    private int? _preeditCursor;
    private ManagedTerminalKeymapResolver _keymapResolver;
    private double _launchFontSize = 13;
    private bool _isApplyingFontSizeCommand;
    private string _commandStatusMessage = string.Empty;
    private bool _isFindVisible;
    private string _findQuery = string.Empty;
    private string _findStatusMessage = string.Empty;
    private TerminalFindResult? _findResult;
    private bool _isFindInFlight;
    private long _findGeneration;

    public ManagedTerminalSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "Interactive terminal");
        AutomationProperties.SetHelpText(
            this,
            "Terminal output is rendered as a cell grid. Use terminal copy mode to review output without sending keys to the session.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetItemStatus(this, "Starting terminal");
        _cursorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _cursorTimer.Tick += OnCursorTimerTick;
        _keySequenceTimer = new DispatcherTimer();
        _keySequenceTimer.Tick += OnKeySequenceTimerTick;
        _imeClient = new TerminalTextInputMethodClient(this);
        _keymapResolver = new ManagedTerminalKeymapResolver(PlatformDefaultKeymap);
        _clipboard = new AvaloniaManagedTerminalClipboard(
            () => TopLevel.GetTopLevel(this)?.Clipboard);
        _linkOpener = new SystemManagedTerminalLinkOpener();
        TextInputMethodClientRequested += OnTextInputMethodClientRequested;
    }

    public event EventHandler? ViewportChanged;

    internal event EventHandler? FocusRequested;

    public event EventHandler<TerminalInputFailureEventArgs>? InputFailed;

    public event EventHandler<TerminalCommandDispatchResult>? CommandDispatched;

    public TerminalRenderProfileSnapshot? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public TerminalKeymapSnapshot? Keymap
    {
        get => GetValue(KeymapProperty);
        set => SetValue(KeymapProperty, value);
    }

    public bool IsPasteConfirmationVisible
    {
        get => _isPasteConfirmationVisible;
        private set => SetAndRaise(
            IsPasteConfirmationVisibleProperty,
            ref _isPasteConfirmationVisible,
            value);
    }

    public bool IsLinkConfirmationVisible
    {
        get => _isLinkConfirmationVisible;
        private set => SetAndRaise(
            IsLinkConfirmationVisibleProperty,
            ref _isLinkConfirmationVisible,
            value);
    }

    public string CommandStatusMessage
    {
        get => _commandStatusMessage;
        private set => SetAndRaise(
            CommandStatusMessageProperty,
            ref _commandStatusMessage,
            value);
    }

    public bool IsFindVisible
    {
        get => _isFindVisible;
        private set => SetAndRaise(IsFindVisibleProperty, ref _isFindVisible, value);
    }

    public string FindQuery
    {
        get => _findQuery;
        private set => SetAndRaise(FindQueryProperty, ref _findQuery, value);
    }

    public string FindStatusMessage
    {
        get => _findStatusMessage;
        private set => SetAndRaise(
            FindStatusMessageProperty,
            ref _findStatusMessage,
            value);
    }

    internal IManagedTerminalInputSink? InputSink
    {
        get => _inputSink;
        set => _inputSink = value;
    }

    internal IManagedTerminalClipboard Clipboard
    {
        get => _clipboard;
        set => _clipboard = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal IManagedTerminalLinkOpener LinkOpener
    {
        get => _linkOpener;
        set => _linkOpener = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal TerminalScreenSnapshot? Snapshot => _snapshot;

    internal string PreeditText => _preeditText;

    internal bool IsInputReady => _isInputReady;

    private bool IsInteractionModalVisible =>
        IsPasteConfirmationVisible || IsLinkConfirmationVisible;

    internal TerminalCellMetrics Metrics =>
        TerminalCellMetrics.Measure(Bounds.Size, Profile);

    public ViewportDescriptor CurrentViewport(double renderScale = 1)
    {
        var metrics = Metrics;
        var hasArrangedContent = Bounds.Width >= metrics.Padding.Left + metrics.Padding.Right + metrics.CellWidth * 2
            && Bounds.Height >= metrics.Padding.Top + metrics.Padding.Bottom + metrics.CellHeight;
        return hasArrangedContent
            ? metrics.ToViewport(Bounds.Size, renderScale)
            : new ViewportDescriptor(
                metrics.CellWidth * 80,
                metrics.CellHeight * 24,
                Math.Max(0.1, renderScale),
                Columns: 80,
                Rows: 24);
    }

    public void UpdateSnapshot(TerminalScreenSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_isAttached && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateSnapshot(snapshot));
            return;
        }

        if (_snapshot?.ContentRevision == snapshot.ContentRevision
            && _snapshot.CursorRow == snapshot.CursorRow
            && _snapshot.CursorColumn == snapshot.CursorColumn
            && _snapshot.IsAlternateScreen == snapshot.IsAlternateScreen)
        {
            _snapshot = snapshot;
            return;
        }

        _snapshot = snapshot;
        UpdateAutomationStatus();
        _imeClient.NotifyCursorRectangleChanged();
        UpdateCursorTimer();
        InvalidateVisual();
    }

    public async ValueTask<TerminalPasteResult> PasteTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_isPasteConfirmationInFlight)
        {
            throw new InvalidOperationException("A terminal paste is already in progress.");
        }

        CancelPendingPaste();
        var sink = RequireInputSink();
        var result = await sink
            .PasteAsync(new TerminalPasteInput(text), cancellationToken)
            .ConfigureAwait(true);
        if (result.RequiresConfirmation)
        {
            _pendingPaste = text;
            IsPasteConfirmationVisible = true;
            InvalidateVisual();
        }

        return result;
    }

    public async ValueTask<TerminalPasteResult?> ConfirmPendingPasteAsync(
        CancellationToken cancellationToken = default)
    {
        if (_pendingPaste is not { } text || _isPasteConfirmationInFlight)
        {
            return null;
        }

        _isPasteConfirmationInFlight = true;
        var generation = _pasteGeneration;
        InvalidateVisual();
        try
        {
            var result = await RequireInputSink()
                .PasteAsync(
                    new TerminalPasteInput(text, ConfirmedUnsafe: true),
                    cancellationToken)
                .ConfigureAwait(true);
            if (generation != _pasteGeneration)
            {
                return result;
            }

            if (result.Sent)
            {
                _pendingPaste = null;
                IsPasteConfirmationVisible = false;
            }
            else
            {
                IsPasteConfirmationVisible = true;
            }

            return result;
        }
        finally
        {
            if (generation == _pasteGeneration)
            {
                _isPasteConfirmationInFlight = false;
                InvalidateVisual();
            }
        }
    }

    public bool CancelPendingPaste()
    {
        if (!IsPasteConfirmationVisible)
        {
            return false;
        }

        if (_isPasteConfirmationInFlight)
        {
            return true;
        }

        _pasteGeneration++;
        _pendingPaste = null;
        IsPasteConfirmationVisible = false;
        InvalidateVisual();
        return true;
    }

    internal async ValueTask<bool> ScrollViewportAsync(
        int lines,
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady
            || IsInteractionModalVisible
            || lines == 0
            || _snapshot is null
            || _snapshot.IsAlternateScreen
            || _snapshot.IsMouseTrackingEnabled)
        {
            return false;
        }

        await RequireInputSink()
            .ScrollViewportAsync(new TerminalViewportScrollInput(lines), cancellationToken)
            .ConfigureAwait(true);
        return true;
    }

    internal async ValueTask<bool> SubmitSelectionAsync(
        TerminalSelectionPhase phase,
        Point point,
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady || IsInteractionModalVisible || _snapshot is null)
        {
            return false;
        }

        var (column, row) = Metrics.CellAt(point);
        await RequireInputSink()
            .UpdateSelectionAsync(new TerminalSelectionInput(phase, column, row), cancellationToken)
            .ConfigureAwait(true);
        return true;
    }

    internal async ValueTask<bool> CopySelectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady
            || IsInteractionModalVisible
            || (Profile?.ClipboardPolicy.WriteAccess
                ?? TerminalClipboardPolicy.Default.WriteAccess) == TerminalClipboardAccess.Deny)
        {
            return false;
        }

        var selection = await RequireInputSink()
            .ReadSelectionAsync(cancellationToken)
            .ConfigureAwait(true);
        if (!selection.HasSelection || selection.Text.Length == 0)
        {
            return false;
        }

        await Clipboard.SetTextAsync(selection.Text, cancellationToken).ConfigureAwait(true);
        return true;
    }

    internal async ValueTask<bool> ActivateLinkAtAsync(
        Point point,
        bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady
            || IsPasteConfirmationVisible
            || Profile?.LinkPolicy == TerminalLinkPolicy.Disabled
            || !TryGetHyperlinkAt(point, out var uri))
        {
            return false;
        }

        if ((Profile?.LinkPolicy ?? TerminalLinkPolicy.ConfirmBeforeOpen)
                == TerminalLinkPolicy.ConfirmBeforeOpen
            && !confirmed)
        {
            _pendingLink = uri;
            IsLinkConfirmationVisible = true;
            InvalidateVisual();
            return true;
        }

        _pendingLink = null;
        IsLinkConfirmationVisible = false;
        await LinkOpener.OpenAsync(uri!, cancellationToken).ConfigureAwait(true);
        InvalidateVisual();
        return true;
    }

    internal async ValueTask<bool> ConfirmPendingLinkAsync(
        CancellationToken cancellationToken = default)
    {
        if (_pendingLink is not { } uri || !IsLinkConfirmationVisible)
        {
            return false;
        }

        if ((Profile?.LinkPolicy ?? TerminalLinkPolicy.ConfirmBeforeOpen)
            == TerminalLinkPolicy.Disabled)
        {
            CancelPendingLink();
            return false;
        }

        _pendingLink = null;
        IsLinkConfirmationVisible = false;
        await LinkOpener.OpenAsync(uri, cancellationToken).ConfigureAwait(true);
        InvalidateVisual();
        return true;
    }

    internal bool CancelPendingInteraction() =>
        CancelPendingPaste() || CancelPendingLink();

    internal bool CancelPendingLink()
    {
        if (!IsLinkConfirmationVisible)
        {
            return false;
        }

        _pendingLink = null;
        IsLinkConfirmationVisible = false;
        InvalidateVisual();
        return true;
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var profile = Profile;
        var palette = profile?.Palette ?? TerminalPalette.GhostShellDark;
        var background = TerminalCellColors.Resolve(
            TerminalCellColor.Default,
            palette,
            foreground: false);
        context.DrawRectangle(new SolidColorBrush(background), null, Bounds.WithX(0).WithY(0));

        if (_snapshot is { } snapshot)
        {
            DrawFrame(context, TerminalRenderLayout.Create(snapshot, profile, Metrics), palette);
        }

        DrawPreedit(context, palette);

        if (IsPasteConfirmationVisible)
        {
            DrawPasteConfirmation(context);
        }
        else if (IsLinkConfirmationVisible)
        {
            DrawLinkConfirmation(context);
        }
        else if (IsFindVisible)
        {
            DrawFind(context);
        }
        else if (!string.IsNullOrEmpty(CommandStatusMessage))
        {
            DrawCommandStatus(context);
        }
    }

    internal ValueTask SubmitTextInputAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_isInputReady || IsInteractionModalVisible)
        {
            return ValueTask.CompletedTask;
        }

        if (IsFindVisible)
        {
            return AppendFindTextAsync(text, cancellationToken);
        }

        UpdatePreedit(null, null);
        if (text.Length > 0)
        {
            ClearCommandStatus();
        }

        return text.Length == 0
            ? ValueTask.CompletedTask
            : RequireInputSink().SendTextAsync(text, cancellationToken);
    }

    internal void UpdatePreedit(string? text, int? cursor)
    {
        _preeditText = text ?? string.Empty;
        _preeditCursor = cursor is null
            ? null
            : Math.Clamp(cursor.Value, 0, _preeditText.Length);
        _imeClient.NotifyCursorRectangleChanged();
        InvalidateVisual();
    }

    internal ValueTask<bool> SubmitKeyAsync(
        Key key,
        AvaloniaKeyModifiers modifiers,
        string? keySymbol = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady || IsInteractionModalVisible)
        {
            return ValueTask.FromResult(false);
        }

        if (ManagedTerminalInput.TryMapSpecialKey(key, modifiers, out var keyStroke)
            && !(key == Key.Space && modifiers == AvaloniaKeyModifiers.None))
        {
            ClearCommandStatus();
            return SendMappedKeyAsync(keyStroke!, cancellationToken);
        }

        if (ManagedTerminalInput.TryEncodeModifiedText(keySymbol, modifiers, out var text))
        {
            ClearCommandStatus();
            return SendMappedTextAsync(text, cancellationToken);
        }

        return ValueTask.FromResult(false);
    }

    internal ValueTask<TerminalCommandDispatchResult> DispatchKeymapShortcutAsync(
        Key key,
        AvaloniaKeyModifiers modifiers,
        string? keySymbol = null,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady || IsInteractionModalVisible)
        {
            return ValueTask.FromResult(TerminalCommandDispatchResult.NotMatched());
        }

        var stroke = ApplicationKeyStrokeMapper.Map(key, modifiers, keySymbol);
        return DispatchKeymapStrokeAsync(stroke, timestamp, cancellationToken);
    }

    internal async ValueTask<TerminalCommandDispatchResult> DispatchKeymapStrokeAsync(
        KeyStroke stroke,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady || IsInteractionModalVisible)
        {
            return TerminalCommandDispatchResult.NotMatched();
        }

        var resolvedAt = timestamp ?? DateTimeOffset.UtcNow;
        var expiration = _keymapResolver.Expire(resolvedAt);
        if (expiration.Kind == TerminalKeyResolutionKind.Expired)
        {
            _keySequenceTimer.Stop();
            if (!await ReplayTerminalStrokesAsync(
                    expiration.ReplayStrokes,
                    cancellationToken))
            {
                return await PublishCommandDispatchAsync(
                    TerminalCommandDispatchResult.UnsupportedSequence(
                        "The expired terminal shortcut could not be passed through safely."));
            }

            ClearCommandStatus();
        }

        var resolution = _keymapResolver.Resolve(stroke, resolvedAt);
        if (resolution.Kind == TerminalKeyResolutionKind.Pending)
        {
            ArmKeySequenceTimer();
        }
        else
        {
            _keySequenceTimer.Stop();
        }

        return resolution.Kind switch
        {
            TerminalKeyResolutionKind.NotHandled =>
                TerminalCommandDispatchResult.NotMatched(),
            TerminalKeyResolutionKind.Pending =>
                await PublishCommandDispatchAsync(TerminalCommandDispatchResult.Pending()),
            TerminalKeyResolutionKind.Rejected => await PublishCommandDispatchAsync(
                TerminalCommandDispatchResult.Rejected(resolution.ShouldHandle)),
            TerminalKeyResolutionKind.PassedThrough =>
                await PublishPassThroughAsync(resolution.ReplayStrokes, cancellationToken),
            TerminalKeyResolutionKind.Matched when resolution.Binding is { } binding =>
                await ExecuteTerminalCommandAsync(binding.CommandId, cancellationToken),
            _ => throw new InvalidOperationException("The terminal key resolver returned an invalid state."),
        };
    }

    internal ValueTask<bool> SubmitMouseAsync(
        TerminalMouseButton button,
        TerminalMouseEventKind kind,
        Point point,
        AvaloniaKeyModifiers modifiers,
        CancellationToken cancellationToken = default)
    {
        if (!_isInputReady
            || IsInteractionModalVisible
            || _snapshot is not { IsMouseTrackingEnabled: true }
            || modifiers.HasFlag(AvaloniaKeyModifiers.Shift))
        {
            return ValueTask.FromResult(false);
        }

        var (column, row) = Metrics.CellAt(point);
        if (kind is TerminalMouseEventKind.Move or TerminalMouseEventKind.Drag
            && _lastPointerCell == (column, row))
        {
            return ValueTask.FromResult(false);
        }

        _lastPointerCell = (column, row);
        return SendMappedMouseAsync(
            new TerminalMouseInput(
                button,
                kind,
                column,
                row,
                ManagedTerminalInput.MapModifiers(modifiers)),
            cancellationToken);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateCursorTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _cursorTimer.Stop();
        _keySequenceTimer.Stop();
        _keymapResolver.Reset();
        ResetFind();
        ClearCommandStatus();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!_isInputReady || IsInteractionModalVisible)
        {
            e.Handled = true;
            return;
        }

        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        e.Handled = true;
        ObserveInputAsync(SubmitTextInputAsync(e.Text));
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        UpdatePreedit(null, null);
        base.OnLostFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_isInputReady)
        {
            e.Handled = true;
            return;
        }

        if (IsPasteConfirmationVisible)
        {
            e.Handled = true;
            if (!_isPasteConfirmationInFlight && e.Key is Key.Return or Key.Enter)
            {
                ObserveInputAsync(ConfirmPendingPasteAsync().AsValueTask());
            }
            else if (!_isPasteConfirmationInFlight && e.Key == Key.Escape)
            {
                CancelPendingPaste();
            }

            return;
        }

        if (IsLinkConfirmationVisible)
        {
            e.Handled = true;
            if (e.Key is Key.Return or Key.Enter)
            {
                ObserveInputAsync(ConfirmPendingLinkAsync().AsValueTask());
            }
            else if (e.Key == Key.Escape)
            {
                CancelPendingLink();
            }

            return;
        }

        if (IsFindVisible)
        {
            var find = HandleFindKeyAsync(e.Key, e.KeyModifiers);
            if (!find.IsCompletedSuccessfully)
            {
                e.Handled = true;
                ObserveInputAsync(find.AsValueTask());
                return;
            }

            e.Handled = find.Result;
            return;
        }

        var command = DispatchKeymapShortcutAsync(e.Key, e.KeyModifiers, e.KeySymbol);
        if (!command.IsCompletedSuccessfully)
        {
            e.Handled = true;
            ObserveInputAsync(command.AsValueTask());
            return;
        }

        if (command.Result.ShouldHandle)
        {
            e.Handled = true;
            return;
        }

        if (_snapshot is { IsAlternateScreen: false, IsMouseTrackingEnabled: false }
            && ManagedTerminalInput.TryMapScrollShortcut(
                e.Key,
                e.KeyModifiers,
                Math.Max(1, _snapshot.Rows - 1),
                out var scrollInput))
        {
            e.Handled = true;
            ObserveInputAsync(RequireInputSink()
                .ScrollViewportAsync(scrollInput!, default));
            return;
        }

        var send = SubmitKeyAsync(e.Key, e.KeyModifiers, e.KeySymbol);
        if (!send.IsCompletedSuccessfully)
        {
            e.Handled = true;
            ObserveInputAsync(send.AsValueTask());
            return;
        }

        e.Handled = send.Result;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        FocusRequested?.Invoke(this, EventArgs.Empty);
        if (!_isInputReady || IsInteractionModalVisible)
        {
            e.Handled = true;
            return;
        }

        Focus();
        var point = e.GetCurrentPoint(this);
        var button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => TerminalMouseButton.Left,
            PointerUpdateKind.MiddleButtonPressed => TerminalMouseButton.Middle,
            PointerUpdateKind.RightButtonPressed => TerminalMouseButton.Right,
            _ => TerminalMouseButton.None,
        };
        if (button == TerminalMouseButton.None)
        {
            return;
        }

        if (button == TerminalMouseButton.Left
            && e.KeyModifiers.HasFlag(AvaloniaKeyModifiers.Control)
            && TryGetHyperlinkAt(point.Position, out _))
        {
            e.Handled = true;
            ObserveInputAsync(ActivateLinkAtAsync(point.Position).AsValueTask());
            return;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        var useLocalSelection = button == TerminalMouseButton.Left
            && (_snapshot?.IsMouseTrackingEnabled != true
                || e.KeyModifiers.HasFlag(AvaloniaKeyModifiers.Shift));
        if (useLocalSelection)
        {
            _isSelecting = true;
            ObserveInputAsync(SubmitSelectionAsync(
                TerminalSelectionPhase.Start,
                point.Position).AsValueTask());
            return;
        }

        if (_snapshot?.IsMouseTrackingEnabled != true)
        {
            e.Pointer.Capture(null);
            e.Handled = false;
            return;
        }

        ObserveInputAsync(SubmitMouseAsync(
            button,
            TerminalMouseEventKind.Down,
            point.Position,
            e.KeyModifiers).AsValueTask());
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isInputReady || IsInteractionModalVisible)
        {
            e.Handled = true;
            return;
        }

        if (_isSelecting)
        {
            e.Handled = true;
            ObserveInputAsync(SubmitSelectionAsync(
                TerminalSelectionPhase.Update,
                e.GetPosition(this)).AsValueTask());
            return;
        }

        if (_snapshot?.IsMouseTrackingEnabled != true)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var button = point.Properties switch
        {
            { IsLeftButtonPressed: true } => TerminalMouseButton.Left,
            { IsMiddleButtonPressed: true } => TerminalMouseButton.Middle,
            { IsRightButtonPressed: true } => TerminalMouseButton.Right,
            _ => TerminalMouseButton.None,
        };
        var kind = button == TerminalMouseButton.None
            ? TerminalMouseEventKind.Move
            : TerminalMouseEventKind.Drag;
        e.Handled = true;
        ObserveInputAsync(SubmitMouseAsync(
            button,
            kind,
            point.Position,
            e.KeyModifiers).AsValueTask());
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isInputReady || IsInteractionModalVisible)
        {
            e.Handled = true;
            _isSelecting = false;
            e.Pointer.Capture(null);
            return;
        }

        if (_isSelecting)
        {
            _isSelecting = false;
            e.Handled = true;
            ObserveInputAsync(SubmitSelectionAsync(
                TerminalSelectionPhase.End,
                e.GetPosition(this)).AsValueTask());
            e.Pointer.Capture(null);
            return;
        }

        var button = e.InitialPressMouseButton switch
        {
            MouseButton.Left => TerminalMouseButton.Left,
            MouseButton.Middle => TerminalMouseButton.Middle,
            MouseButton.Right => TerminalMouseButton.Right,
            _ => TerminalMouseButton.None,
        };
        if (button != TerminalMouseButton.None)
        {
            e.Handled = _snapshot?.IsMouseTrackingEnabled == true;
            ObserveInputAsync(SubmitMouseAsync(
                button,
                TerminalMouseEventKind.Up,
                e.GetPosition(this),
                e.KeyModifiers).AsValueTask());
        }

        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_isInputReady || IsInteractionModalVisible)
        {
            e.Handled = true;
            return;
        }

        if (_snapshot is null || e.Delta.Y == 0)
        {
            return;
        }

        var up = e.Delta.Y > 0;
        e.Handled = true;
        if (_snapshot.IsMouseTrackingEnabled)
        {
            ObserveInputAsync(SubmitMouseAsync(
                up ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown,
                up ? TerminalMouseEventKind.WheelUp : TerminalMouseEventKind.WheelDown,
                e.GetPosition(this),
                e.KeyModifiers).AsValueTask());
        }
        else if (!_snapshot.IsAlternateScreen)
        {
            ObserveInputAsync(ScrollViewportAsync(up ? -3 : 3).AsValueTask());
        }
        else
        {
            e.Handled = false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            _imeClient.NotifyCursorRectangleChanged();
        }
        else if (change.Property == ProfileProperty)
        {
            if (!_isApplyingFontSizeCommand)
            {
                _launchFontSize = Profile?.FontSize ?? 13;
            }

            if (Profile?.LinkPolicy == TerminalLinkPolicy.Disabled)
            {
                CancelPendingLink();
            }

            InputMethod.SetIsInputMethodEnabled(
                this,
                _isInputReady && (Profile?.ImeEnabled ?? true));
            if (Profile?.ImeEnabled == false)
            {
                UpdatePreedit(null, null);
            }

            UpdateCursorTimer();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            _imeClient.NotifyTextViewChanged();
            _imeClient.NotifyCursorRectangleChanged();
            InvalidateVisual();
        }
        else if (change.Property == KeymapProperty)
        {
            _keySequenceTimer.Stop();
            _keymapResolver = new ManagedTerminalKeymapResolver(Keymap ?? PlatformDefaultKeymap);
            ClearCommandStatus();
        }
    }

    private async ValueTask<bool> SendMappedKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken)
    {
        await RequireInputSink().SendKeyAsync(keyStroke, cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async ValueTask<bool> SendMappedTextAsync(
        string text,
        CancellationToken cancellationToken)
    {
        await RequireInputSink().SendTextAsync(text, cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async ValueTask<TerminalCommandDispatchResult> PublishPassThroughAsync(
        IReadOnlyList<KeyStroke>? strokes,
        CancellationToken cancellationToken)
    {
        if (!await ReplayTerminalStrokesAsync(strokes, cancellationToken))
        {
            return await PublishCommandDispatchAsync(
                TerminalCommandDispatchResult.UnsupportedSequence(
                    "The terminal shortcut could not be passed through safely."));
        }

        return await PublishCommandDispatchAsync(TerminalCommandDispatchResult.PassedThrough());
    }

    private async ValueTask<bool> ReplayTerminalStrokesAsync(
        IReadOnlyList<KeyStroke>? strokes,
        CancellationToken cancellationToken)
    {
        if (strokes is null || strokes.Count == 0)
        {
            return true;
        }

        var replay = new (TerminalKeyStroke? KeyStroke, string Text)[strokes.Count];
        for (var index = 0; index < strokes.Count; index++)
        {
            if (!ManagedTerminalInput.TryMapReplayStroke(
                    strokes[index],
                    out var keyStroke,
                    out var text))
            {
                return false;
            }

            replay[index] = (keyStroke, text);
        }

        var sink = RequireInputSink();
        foreach (var input in replay)
        {
            if (input.KeyStroke is { } keyStroke)
            {
                await sink.SendKeyAsync(keyStroke, cancellationToken);
            }
            else
            {
                await sink.SendTextAsync(input.Text, cancellationToken);
            }
        }

        return true;
    }

    private async ValueTask<bool> SendMappedMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken)
    {
        await RequireInputSink().SendMouseAsync(mouseInput, cancellationToken).ConfigureAwait(true);
        return true;
    }

    internal async ValueTask<bool> PasteFromClipboardAsync(
        CancellationToken cancellationToken = default)
    {
        if ((Profile?.ClipboardPolicy.ReadAccess
                ?? TerminalClipboardPolicy.Default.ReadAccess) == TerminalClipboardAccess.Deny)
        {
            return false;
        }

        // A user-initiated paste gesture is the one-shot consent represented by Ask.
        // Process-initiated clipboard reads remain brokered (and fail closed) in the engine.
        var text = await Clipboard.TryGetTextAsync(cancellationToken);
        if (!string.IsNullOrEmpty(text))
        {
            _ = await PasteTextAsync(text, cancellationToken);
            return true;
        }

        return false;
    }

    internal async ValueTask<bool> BeginFindAsync(
        CancellationToken cancellationToken = default)
    {
        var capability = await RequireInputSink()
            .FindAsync(new TerminalFindInput(string.Empty), cancellationToken)
            .ConfigureAwait(true);
        if (capability is null)
        {
            return false;
        }

        _findGeneration++;
        _findResult = TerminalFindResult.Empty;
        _isFindInFlight = false;
        FindQuery = string.Empty;
        IsFindVisible = true;
        UpdateFindStatus();
        UpdateAutomationStatus();
        UpdatePreedit(null, null);
        InvalidateVisual();
        return true;
    }

    internal async ValueTask CloseFindAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsFindVisible)
        {
            return;
        }

        ResetFind();
        _ = await RequireInputSink()
            .FindAsync(new TerminalFindInput(string.Empty), cancellationToken)
            .ConfigureAwait(true);
    }

    internal ValueTask<bool> HandleFindKeyAsync(
        Key key,
        AvaloniaKeyModifiers modifiers,
        CancellationToken cancellationToken = default)
    {
        if (!IsFindVisible)
        {
            return ValueTask.FromResult(false);
        }

        if (key == Key.Escape)
        {
            return CloseFindAndHandleAsync(cancellationToken);
        }

        if (key is Key.Return or Key.Enter or Key.F3)
        {
            var direction = modifiers.HasFlag(AvaloniaKeyModifiers.Shift) ? -1 : 1;
            var requested = (_findResult?.SelectedMatchIndex ?? -1) + direction;
            return RunFindAndHandleAsync(requested, cancellationToken);
        }

        if (key == Key.Back)
        {
            if (FindQuery.Length == 0)
            {
                return ValueTask.FromResult(true);
            }

            var elements = StringInfo.ParseCombiningCharacters(FindQuery);
            FindQuery = FindQuery[..elements[^1]];
            return RunFindAndHandleAsync(0, cancellationToken);
        }

        var commandModifiers = modifiers
            & (AvaloniaKeyModifiers.Control
                | AvaloniaKeyModifiers.Alt
                | AvaloniaKeyModifiers.Meta);
        return ValueTask.FromResult(commandModifiers != AvaloniaKeyModifiers.None);
    }

    private async ValueTask AppendFindTextAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (FindQuery.Length + text.Length > TerminalFindInput.MaximumQueryLength)
        {
            FindStatusMessage = $"Find queries are limited to {TerminalFindInput.MaximumQueryLength} characters.";
            UpdateAutomationStatus();
            InvalidateVisual();
            return;
        }

        FindQuery += text;
        _ = await RunFindAsync(0, cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask<bool> CloseFindAndHandleAsync(CancellationToken cancellationToken)
    {
        await CloseFindAsync(cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async ValueTask<bool> RunFindAndHandleAsync(
        int requestedMatchIndex,
        CancellationToken cancellationToken)
    {
        _ = await RunFindAsync(requestedMatchIndex, cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async ValueTask<TerminalFindResult?> RunFindAsync(
        int requestedMatchIndex,
        CancellationToken cancellationToken)
    {
        if (!IsFindVisible)
        {
            return null;
        }

        var generation = ++_findGeneration;
        _isFindInFlight = true;
        UpdateFindStatus();
        UpdateAutomationStatus();
        InvalidateVisual();
        var result = await RequireInputSink()
            .FindAsync(
                new TerminalFindInput(FindQuery, requestedMatchIndex),
                cancellationToken)
            .ConfigureAwait(true);
        if (generation != _findGeneration || !IsFindVisible)
        {
            return result;
        }

        _isFindInFlight = false;
        _findResult = result;
        UpdateFindStatus();
        UpdateAutomationStatus();
        InvalidateVisual();
        return result;
    }

    private void UpdateFindStatus()
    {
        if (_isFindInFlight)
        {
            FindStatusMessage = "Searching the terminal buffer…";
            return;
        }

        if (FindQuery.Length == 0)
        {
            FindStatusMessage = "Type to search · Enter next · Shift+Enter previous · Esc close";
            return;
        }

        FindStatusMessage = _findResult switch
        {
            null => "Full-buffer find is unavailable for this terminal backend.",
            { MatchCount: 0, IsScanTruncated: true } =>
                "No match in the scanned portion of the terminal buffer.",
            { MatchCount: 0 } => "No matches in the terminal buffer.",
            { } result =>
                $"Match {result.SelectedMatchIndex + 1:N0} of {result.MatchCount:N0}"
                + (result.IsScanTruncated ? " · results truncated" : string.Empty)
                + " · Enter next · Shift+Enter previous · Esc close",
        };
    }

    private void ResetFind()
    {
        _findGeneration++;
        _isFindInFlight = false;
        _findResult = null;
        FindQuery = string.Empty;
        FindStatusMessage = string.Empty;
        IsFindVisible = false;
        UpdateAutomationStatus();
        InvalidateVisual();
    }

    private async ValueTask<TerminalCommandDispatchResult> ExecuteTerminalCommandAsync(
        CommandId commandId,
        CancellationToken cancellationToken)
    {
        TerminalCommandDispatchResult result;
        if (commandId == BuiltInCommands.Copy)
        {
            result = await CopySelectionAsync(cancellationToken)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "Nothing can be copied under the current selection and clipboard policy.");
        }
        else if (commandId == BuiltInCommands.Paste)
        {
            result = await PasteFromClipboardAsync(cancellationToken)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "The clipboard is empty or terminal clipboard reads are disabled.");
        }
        else if (commandId == BuiltInCommands.SelectAll)
        {
            result = await SelectVisibleTerminalAsync(cancellationToken)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "Visible terminal content is not ready to select.");
        }
        else if (commandId == BuiltInCommands.IncreaseFontSize)
        {
            result = ApplyFontSize((Profile?.FontSize ?? _launchFontSize) + 1)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "A terminal render profile is not available.");
        }
        else if (commandId == BuiltInCommands.DecreaseFontSize)
        {
            result = ApplyFontSize((Profile?.FontSize ?? _launchFontSize) - 1)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "A terminal render profile is not available.");
        }
        else if (commandId == BuiltInCommands.ResetFontSize)
        {
            result = ApplyFontSize(_launchFontSize)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "A terminal render profile is not available.");
        }
        else if (commandId == BuiltInCommands.Find)
        {
            result = await BeginFindAsync(cancellationToken)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "Full-buffer find is unavailable for this terminal backend.");
        }
        else if (commandId == BuiltInCommands.ClearScrollback)
        {
            result = await RequireInputSink().ClearScrollbackAsync(cancellationToken)
                ? TerminalCommandDispatchResult.Executed(commandId)
                : TerminalCommandDispatchResult.Unavailable(
                    commandId,
                    "Clearing scrollback is unavailable for this terminal backend.");
        }
        else if (CanonicalTerminalEditingText(commandId) is { } text)
        {
            await RequireInputSink().SendTextAsync(text, cancellationToken);
            result = TerminalCommandDispatchResult.Executed(commandId);
        }
        else
        {
            var message = BuiltInCommands.Registry.Contains(commandId)
                ? $"The managed terminal does not support '{commandId}' yet."
                : $"Terminal command '{commandId}' is not available in this version.";
            result = TerminalCommandDispatchResult.Unsupported(commandId, message);
        }

        return await PublishCommandDispatchAsync(result);
    }

    private bool ApplyFontSize(double requestedSize)
    {
        if (Profile is not { } profile)
        {
            return false;
        }

        var fontSize = Math.Clamp(requestedSize, 6, 96);
        if (fontSize == profile.FontSize)
        {
            return true;
        }

        _isApplyingFontSizeCommand = true;
        try
        {
            Profile = new TerminalRenderProfileSnapshot(
                fontSize,
                profile.CursorStyle,
                profile.CursorBlink,
                profile.ScrollbackLines,
                profile.Palette,
                profile.FontFamily,
                profile.LineHeight,
                profile.ClipboardPolicy,
                profile.LinkPolicy,
                profile.ImeEnabled,
                profile.ShellIntegration,
                profile.BellMode,
                profile.Compatibility);
        }
        finally
        {
            _isApplyingFontSizeCommand = false;
        }

        return true;
    }

    private async ValueTask<bool> SelectVisibleTerminalAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is not { } snapshot)
        {
            return false;
        }

        var sink = RequireInputSink();
        await sink.UpdateSelectionAsync(
            new TerminalSelectionInput(TerminalSelectionPhase.Start, 0, 0),
            cancellationToken);
        await sink.UpdateSelectionAsync(
            new TerminalSelectionInput(
                TerminalSelectionPhase.End,
                Math.Max(0, snapshot.Columns - 1),
                Math.Max(0, snapshot.Rows - 1)),
            cancellationToken);
        return true;
    }

    private ValueTask<TerminalCommandDispatchResult> PublishCommandDispatchAsync(
        TerminalCommandDispatchResult result)
    {
        if (result.Status != TerminalCommandDispatchResult.Outcome.NotMatched)
        {
            CommandStatusMessage = result.Status is
                TerminalCommandDispatchResult.Outcome.Pending
                or TerminalCommandDispatchResult.Outcome.Unavailable
                or TerminalCommandDispatchResult.Outcome.Unsupported
                or TerminalCommandDispatchResult.Outcome.Rejected
                    ? result.Message
                    : string.Empty;
            UpdateAutomationStatus();
            InvalidateVisual();
            CommandDispatched?.Invoke(this, result);
        }

        return ValueTask.FromResult(result);
    }

    private void ClearCommandStatus()
    {
        if (string.IsNullOrEmpty(CommandStatusMessage))
        {
            return;
        }

        CommandStatusMessage = string.Empty;
        UpdateAutomationStatus();
        InvalidateVisual();
    }

    private void DrawCommandStatus(DrawingContext context)
    {
        var message = new FormattedText(
            CommandStatusMessage,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(Profile?.FontFamily ?? "JetBrains Mono, Menlo, Consolas, monospace"),
            Math.Max(10, (Profile?.FontSize ?? 13) - 2),
            ConfirmationText);
        var box = new Rect(
            12,
            Math.Max(12, Bounds.Height - message.Height - 28),
            Math.Min(Math.Max(240, message.Width + 24), Math.Max(240, Bounds.Width - 24)),
            message.Height + 16);
        context.DrawRectangle(
            ConfirmationBackground,
            new Pen(ConfirmationBorder, 1),
            box,
            8,
            8);
        context.DrawText(message, new Point(box.X + 12, box.Y + 8));
    }

    private static string? CanonicalTerminalEditingText(CommandId commandId)
    {
        if (commandId == BuiltInCommands.MoveWordLeft)
        {
            return "\u001bb";
        }

        if (commandId == BuiltInCommands.MoveWordRight)
        {
            return "\u001bf";
        }

        if (commandId == BuiltInCommands.DeleteWordBackward)
        {
            return "\u0017";
        }

        if (commandId == BuiltInCommands.DeleteWordForward)
        {
            return "\u001bd";
        }

        if (commandId == BuiltInCommands.MoveToLineStart)
        {
            return "\u0001";
        }

        if (commandId == BuiltInCommands.MoveToLineEnd)
        {
            return "\u0005";
        }

        if (commandId == BuiltInCommands.SendInterrupt)
        {
            return "\u0003";
        }

        if (commandId == BuiltInCommands.SendEndOfFile)
        {
            return "\u0004";
        }

        return commandId == BuiltInCommands.ClearScreen ? "\u000c" : null;
    }

    private void DrawFrame(
        DrawingContext context,
        TerminalRenderFrame frame,
        TerminalPalette palette)
    {
        var metrics = Metrics;
        var brushes = new Dictionary<Color, SolidColorBrush>();
        foreach (var cell in frame.Cells)
        {
            var bounds = metrics.CellBounds(cell.Row, cell.Column, cell.Width);
            var background = BrushFor(cell.Background, brushes);
            context.DrawRectangle(background, null, bounds);
            if (cell.Text.Length == 0
                || cell.Style.HasFlag(TerminalCellStyle.Invisible))
            {
                continue;
            }

            if (cell.Style.HasFlag(TerminalCellStyle.Blink) && !_blinkVisible)
            {
                continue;
            }

            var foreground = BrushFor(cell.Foreground, brushes);
            var typeface = new Typeface(
                Profile?.FontFamily ?? "JetBrains Mono, Menlo, Consolas, monospace",
                cell.Style.HasFlag(TerminalCellStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
                cell.Style.HasFlag(TerminalCellStyle.Bold) ? FontWeight.Bold : FontWeight.Normal);
            var text = new FormattedText(
                cell.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                Profile?.FontSize ?? 13,
                foreground);
            var origin = new Point(bounds.X, bounds.Y + Math.Max(0, (bounds.Height - text.Height) / 2));
            context.DrawText(text, origin);
            DrawDecorations(context, cell, bounds, foreground);
        }

        if (_cursorVisible && frame.IsCursorVisible)
        {
            DrawCursor(context, frame, palette, metrics);
        }
    }

    private void DrawCursor(
        DrawingContext context,
        TerminalRenderFrame frame,
        TerminalPalette palette,
        TerminalCellMetrics metrics)
    {
        var bounds = metrics.CellBounds(frame.CursorRow, frame.CursorColumn);
        var cursorColor = TerminalCellColors.Resolve(
            new TerminalCellColor(
                TerminalColorMode.Rgb,
                palette.Cursor.Red << 16 | palette.Cursor.Green << 8 | palette.Cursor.Blue),
            palette,
            foreground: true);
        var brush = new SolidColorBrush(Color.FromArgb(
            190,
            cursorColor.R,
            cursorColor.G,
            cursorColor.B));
        switch (Profile?.CursorStyle ?? TerminalCursorStyle.Block)
        {
            case TerminalCursorStyle.Block:
                context.DrawRectangle(brush, null, bounds);
                break;
            case TerminalCursorStyle.Bar:
                context.DrawRectangle(brush, null, bounds.WithWidth(Math.Max(1.5, bounds.Width * 0.12)));
                break;
            case TerminalCursorStyle.Underline:
                context.DrawRectangle(
                    brush,
                    null,
                    new Rect(bounds.X, bounds.Bottom - 2, bounds.Width, 2));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Profile), Profile?.CursorStyle, null);
        }
    }

    private static void DrawDecorations(
        DrawingContext context,
        TerminalRenderCell cell,
        Rect bounds,
        IBrush foreground)
    {
        var pen = new Pen(foreground, 1);
        if (cell.Style.HasFlag(TerminalCellStyle.Underline))
        {
            context.DrawLine(
                pen,
                new Point(bounds.Left, bounds.Bottom - 1.5),
                new Point(bounds.Right, bounds.Bottom - 1.5));
        }

        if (cell.Style.HasFlag(TerminalCellStyle.Strikethrough))
        {
            context.DrawLine(
                pen,
                new Point(bounds.Left, bounds.Center.Y),
                new Point(bounds.Right, bounds.Center.Y));
        }

        if (cell.Style.HasFlag(TerminalCellStyle.Overline))
        {
            context.DrawLine(
                pen,
                new Point(bounds.Left, bounds.Top + 1),
                new Point(bounds.Right, bounds.Top + 1));
        }
    }

    private void DrawPasteConfirmation(DrawingContext context)
    {
        var text = _pendingPaste ?? string.Empty;
        var lineCount = text.Count(character => character == '\n') + 1;
        var message = $"Paste {lineCount:N0} lines ({text.Length:N0} characters)?";
        var hint = _isPasteConfirmationInFlight
            ? "Pasting safely…"
            : "Enter to paste  ·  Esc to cancel";
        var box = new Rect(
            12,
            Math.Max(12, Bounds.Height - 76),
            Math.Max(220, Bounds.Width - 24),
            62);
        context.DrawRectangle(
            ConfirmationBackground,
            new Pen(ConfirmationBorder, 1),
            box,
            8,
            8);
        context.DrawText(
            new FormattedText(
                message,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter, SF Pro Text, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold),
                11,
                ConfirmationText),
            new Point(box.X + 14, box.Y + 10));
        context.DrawText(
            new FormattedText(
                hint,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("JetBrains Mono, Menlo, Consolas, monospace"),
                9,
                ConfirmationAccent),
            new Point(box.X + 14, box.Y + 35));
    }

    private void DrawLinkConfirmation(DrawingContext context)
    {
        var display = _pendingLink?.AbsoluteUri ?? string.Empty;
        if (display.Length > 96)
        {
            display = display[..93] + "…";
        }

        var box = new Rect(
            12,
            Math.Max(12, Bounds.Height - 76),
            Math.Max(220, Bounds.Width - 24),
            62);
        context.DrawRectangle(
            ConfirmationBackground,
            new Pen(ConfirmationBorder, 1),
            box,
            8,
            8);
        context.DrawText(
            new FormattedText(
                $"Open link?  {display}",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter, SF Pro Text, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold),
                11,
                ConfirmationText),
            new Point(box.X + 14, box.Y + 10));
        context.DrawText(
            new FormattedText(
                "Enter to open  ·  Esc to cancel",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("JetBrains Mono, Menlo, Consolas, monospace"),
                9,
                ConfirmationAccent),
            new Point(box.X + 14, box.Y + 35));
    }

    private void DrawFind(DrawingContext context)
    {
        var width = Math.Min(460, Math.Max(260, Bounds.Width - 24));
        var box = new Rect(
            Math.Max(12, Bounds.Width - width - 12),
            12,
            width,
            66);
        context.DrawRectangle(
            ConfirmationBackground,
            new Pen(ConfirmationBorder, 1),
            box,
            8,
            8);

        var query = TruncateFindText(FindQuery, 64);
        var prompt = query.Length == 0 ? "Find: ▏" : $"Find: {query}▏";
        context.DrawText(
            new FormattedText(
                prompt,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("JetBrains Mono, Menlo, Consolas, monospace"),
                11,
                ConfirmationText),
            new Point(box.X + 14, box.Y + 10));
        context.DrawText(
            new FormattedText(
                TruncateFindText(FindStatusMessage, 92),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter, SF Pro Text, Segoe UI, sans-serif"),
                9,
                ConfirmationAccent),
            new Point(box.X + 14, box.Y + 39));
    }

    private static string TruncateFindText(string text, int maximumTextElements)
    {
        var elements = StringInfo.ParseCombiningCharacters(text);
        return elements.Length <= maximumTextElements
            ? text
            : text[..elements[maximumTextElements]] + "…";
    }

    private bool TryGetHyperlinkAt(Point point, out Uri? uri)
    {
        uri = null;
        if (_snapshot is not { } snapshot)
        {
            return false;
        }

        var (targetColumn, targetRow) = Metrics.CellAt(point);
        if (targetRow >= snapshot.StructuredRows.Count)
        {
            return false;
        }

        var column = 0;
        foreach (var cell in snapshot.StructuredRows[targetRow].Cells)
        {
            if (cell.Width == 0)
            {
                continue;
            }

            if (targetColumn >= column
                && targetColumn < column + cell.Width
                && ManagedTerminalLinks.TryCreateAllowedUri(cell.Hyperlink, out uri))
            {
                return true;
            }

            column += cell.Width;
        }

        uri = null;
        return false;
    }

    private void DrawPreedit(DrawingContext context, TerminalPalette palette)
    {
        if (_preeditText.Length == 0 || Profile?.ImeEnabled == false)
        {
            return;
        }

        var foreground = new SolidColorBrush(TerminalCellColors.Resolve(
            TerminalCellColor.Default,
            palette,
            foreground: true));
        var background = new SolidColorBrush(Color.FromArgb(
            230,
            palette.SelectionBackground.Red,
            palette.SelectionBackground.Green,
            palette.SelectionBackground.Blue));
        var text = new FormattedText(
            _preeditText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(Profile?.FontFamily ?? "JetBrains Mono, Menlo, Consolas, monospace"),
            Profile?.FontSize ?? 13,
            foreground);
        var snapshot = _snapshot;
        var metrics = Metrics;
        var cellCount = Math.Max(1, (int)Math.Ceiling(text.Width / metrics.CellWidth));
        var bounds = metrics.CellBounds(
            snapshot?.CursorRow ?? 0,
            snapshot?.CursorColumn ?? 0,
            cellCount);
        context.DrawRectangle(background, null, bounds);
        context.DrawText(
            text,
            new Point(bounds.X, bounds.Y + Math.Max(0, (bounds.Height - text.Height) / 2)));
        context.DrawLine(
            new Pen(ConfirmationAccent, 1.5),
            new Point(bounds.Left, bounds.Bottom - 1),
            new Point(bounds.Right, bounds.Bottom - 1));

        if (_preeditCursor is { } cursor)
        {
            var prefix = new FormattedText(
                _preeditText[..Math.Min(cursor, _preeditText.Length)],
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(Profile?.FontFamily ?? "JetBrains Mono, Menlo, Consolas, monospace"),
                Profile?.FontSize ?? 13,
                foreground);
            var cursorX = bounds.X + prefix.Width;
            context.DrawLine(
                new Pen(ConfirmationText, 1),
                new Point(cursorX, bounds.Top + 2),
                new Point(cursorX, bounds.Bottom - 2));
        }
    }

    private void OnTextInputMethodClientRequested(
        object? sender,
        TextInputMethodClientRequestedEventArgs e)
    {
        _ = sender;
        e.Client = !_isInputReady || Profile?.ImeEnabled == false ? null : _imeClient;
    }

    internal void SetInputReady(bool isReady)
    {
        if (_isInputReady == isReady)
        {
            return;
        }

        _isInputReady = isReady;
        UpdateAutomationStatus();
        Focusable = isReady;
        InputMethod.SetIsInputMethodEnabled(
            this,
            isReady && (Profile?.ImeEnabled ?? true));
        if (!isReady)
        {
            _keySequenceTimer.Stop();
            _keymapResolver.Reset();
            ResetPendingPaste();
            ResetPendingLink();
            ResetFind();
            _isSelecting = false;
            UpdatePreedit(null, null);
            ClearCommandStatus();
        }
    }

    private void UpdateAutomationStatus()
    {
        var state = _isInputReady ? "input ready" : "input unavailable";
        var commandStatus = string.IsNullOrEmpty(CommandStatusMessage)
            ? string.Empty
            : $", {CommandStatusMessage}";
        var findStatus = IsFindVisible
            ? $", find query '{FindQuery}', {FindStatusMessage}"
            : string.Empty;
        if (_snapshot is not { } snapshot)
        {
            AutomationProperties.SetItemStatus(this, state + commandStatus + findStatus);
            return;
        }

        var screen = snapshot.IsAlternateScreen ? "alternate screen" : "normal screen";
        AutomationProperties.SetItemStatus(
            this,
            $"{snapshot.Rows} rows by {snapshot.Columns} columns, {screen}, {state}{commandStatus}{findStatus}");
    }

    internal Rect GetImeCursorRectangle()
    {
        var metrics = Metrics;
        var snapshot = _snapshot;
        var row = Math.Clamp(snapshot?.CursorRow ?? 0, 0, metrics.Rows - 1);
        var column = Math.Clamp(snapshot?.CursorColumn ?? 0, 0, metrics.Columns - 1);
        var cell = metrics.CellBounds(row, column);
        var caretOffset = 0d;
        if (_preeditCursor is { } preeditCursor && _preeditText.Length > 0)
        {
            var prefix = _preeditText[..Math.Min(preeditCursor, _preeditText.Length)];
            try
            {
                caretOffset = new FormattedText(
                    prefix,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(Profile?.FontFamily ?? "JetBrains Mono, Menlo, Consolas, monospace"),
                    Profile?.FontSize ?? 13,
                    Brushes.White).Width;
            }
            catch (InvalidOperationException)
            {
                caretOffset = prefix.EnumerateRunes().Count() * metrics.CellWidth;
            }
        }

        var surfaceWidth = Math.Max(1, Bounds.Width);
        var surfaceHeight = Math.Max(1, Bounds.Height);
        var minimumX = Math.Min(metrics.Padding.Left, surfaceWidth - 1);
        var minimumY = Math.Min(metrics.Padding.Top, surfaceHeight - 1);
        var x = Math.Clamp(cell.X + caretOffset, minimumX, surfaceWidth - 1);
        var y = Math.Clamp(cell.Y, minimumY, surfaceHeight - 1);
        return new Rect(
            x,
            y,
            1,
            Math.Max(1, Math.Min(cell.Height, surfaceHeight - y)));
    }

    private void ResetPendingPaste()
    {
        _pasteGeneration++;
        _pendingPaste = null;
        _isPasteConfirmationInFlight = false;
        IsPasteConfirmationVisible = false;
        InvalidateVisual();
    }

    private void ResetPendingLink()
    {
        _pendingLink = null;
        IsLinkConfirmationVisible = false;
        InvalidateVisual();
    }

    private void UpdateCursorTimer()
    {
        var hasBlinkingCells = _snapshot?.StructuredRows.Any(row =>
            row.Cells.Any(cell => cell.Style.HasFlag(TerminalCellStyle.Blink))) == true;
        if (_isAttached && (Profile?.CursorBlink == true || hasBlinkingCells))
        {
            _cursorTimer.Start();
        }
        else
        {
            _cursorTimer.Stop();
            _cursorVisible = true;
            _blinkVisible = true;
        }
    }

    private void OnCursorTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _blinkVisible = !_blinkVisible;
        _cursorVisible = Profile?.CursorBlink == true
            ? !_cursorVisible
            : true;
        InvalidateVisual();
    }

    private void ArmKeySequenceTimer()
    {
        _keySequenceTimer.Stop();
        _keySequenceTimer.Interval = _keymapResolver.SequenceTimeout;
        _keySequenceTimer.Start();
    }

    private void OnKeySequenceTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _keySequenceTimer.Stop();
        if (_keymapResolver.PendingDeadline is { } deadline)
        {
            ObserveInputAsync(ExpirePendingKeySequenceAsync(
                deadline + TimeSpan.FromTicks(1)).AsValueTask());
        }
    }

    internal async ValueTask<bool> ExpirePendingKeySequenceAsync(
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        var expiration = _keymapResolver.Expire(timestamp);
        if (expiration.Kind != TerminalKeyResolutionKind.Expired)
        {
            return false;
        }

        _keySequenceTimer.Stop();
        if (!await ReplayTerminalStrokesAsync(expiration.ReplayStrokes, cancellationToken))
        {
            _ = await PublishCommandDispatchAsync(
                TerminalCommandDispatchResult.UnsupportedSequence(
                    "The expired terminal shortcut could not be passed through safely."));
            return true;
        }

        ClearCommandStatus();
        return true;
    }

    private void ObserveInputAsync(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            _ = ObserveInputCoreAsync(task);
        }
    }

    private async Task ObserveInputCoreAsync(ValueTask task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Managed terminal input failed: {0}", exception);
            InputFailed?.Invoke(this, new TerminalInputFailureEventArgs(exception));
        }
    }

    private static SolidColorBrush BrushFor(
        Color color,
        IDictionary<Color, SolidColorBrush> cache)
    {
        if (!cache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            cache.Add(color, brush);
        }

        return brush;
    }

    private IManagedTerminalInputSink RequireInputSink() => _inputSink
        ?? throw new InvalidOperationException("The managed terminal input sink is unavailable.");
}

public sealed class TerminalInputFailureEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } =
        exception ?? throw new ArgumentNullException(nameof(exception));
}

internal static class TerminalInputValueTask
{
    public static async ValueTask AsValueTask<T>(this ValueTask<T> task) =>
        _ = await task;
}
