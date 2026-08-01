using System.Text;
using GhostShell.Application;
using GhostShell.Terminal.GhosttyVt;

namespace GhostShell.Terminal;

internal sealed partial class GhosttyVtTerminalSession
{
    private const int MaximumFindMatches = 4_096;

    public unsafe ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollInput);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var behavior = new GhosttyVtScrollViewport
            {
                Tag = GhosttyVtScrollViewportTag.Delta,
                Value = new GhosttyVtScrollViewportValue
                {
                    Delta = scrollInput.Lines,
                },
            };
            GhosttyVtNative.TerminalScrollViewport(_terminal, behavior);
            MarkContentChangedUnsafe();
        }

        return ValueTask.CompletedTask;
    }

    public unsafe ValueTask ClearScrollbackAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "The pinned libghostty-vt API does not yet expose a history-only clear operation."));

    public unsafe ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectionInput);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (selectionInput.Phase == TerminalSelectionPhase.Clear)
            {
                ClearSelectionUnsafe();
                MarkContentChangedUnsafe();
                return ValueTask.CompletedTask;
            }

            var column = Math.Clamp(selectionInput.Column, 0, _columns - 1);
            var row = Math.Clamp(selectionInput.Row, 0, _rows - 1);
            var endpoint = GetViewportGridReferenceUnsafe(column, row);
            GhosttyVtSelection selection;
            if (selectionInput.Phase == TerminalSelectionPhase.Start)
            {
                selection = GhosttyVtSelection.CreateSized();
                selection.Start = endpoint;
                selection.End = endpoint;
                _selectionAnchor = new SelectionAnchor(column, row);
            }
            else
            {
                selection = GhosttyVtSelection.CreateSized();
                var result = GhosttyVtNative.TerminalGet(
                    _terminal,
                    GhosttyVtTerminalData.Selection,
                    &selection);
                if (result == GhosttyVtResult.NoValue)
                {
                    var anchor = _selectionAnchor ?? new SelectionAnchor(column, row);
                    selection = GhosttyVtSelection.CreateSized();
                    selection.Start = GetViewportGridReferenceUnsafe(anchor.Column, anchor.Row);
                }
                else
                {
                    EnsureSuccess(result, "read terminal selection");
                }

                selection.End = endpoint;
            }

            EnsureSuccess(
                GhosttyVtNative.TerminalSet(
                    _terminal,
                    GhosttyVtTerminalOption.Selection,
                    &selection),
                "update terminal selection");
            MarkContentChangedUnsafe();
        }

        return ValueTask.CompletedTask;
    }

    public unsafe ValueTask<TerminalSelectionText> ReadSelectionAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var options = GhosttyVtSelectionFormatOptions.CreateSized();
            options.Format = GhosttyVtFormatterFormat.Plain;
            options.Unwrap = 1;
            options.Trim = 1;
            options.Selection = 0;

            nuint required = 0;
            var result = GhosttyVtNative.TerminalSelectionFormat(
                _terminal,
                options,
                null,
                0,
                &required);
            if (result == GhosttyVtResult.NoValue)
            {
                return ValueTask.FromResult(
                    new TerminalSelectionText(string.Empty, false, false));
            }

            if (result != GhosttyVtResult.OutOfSpace && result != GhosttyVtResult.Success)
            {
                EnsureSuccess(result, "measure terminal selection");
            }

            if (required == 0)
            {
                return ValueTask.FromResult(
                    new TerminalSelectionText(string.Empty, true, false));
            }

            if (required > int.MaxValue)
            {
                throw new InvalidOperationException("The terminal selection is too large to copy.");
            }

            var bytes = new byte[checked((int)required)];
            fixed (byte* output = bytes)
            {
                EnsureSuccess(
                    GhosttyVtNative.TerminalSelectionFormat(
                        _terminal,
                        options,
                        output,
                        checked((nuint)bytes.Length),
                        &required),
                    "copy terminal selection");
            }

            var text = Encoding.UTF8.GetString(bytes, 0, checked((int)required));
            var truncated = text.Length > TerminalSelectionText.MaximumCharacters;
            if (truncated)
            {
                var length = TerminalSelectionText.MaximumCharacters;
                if (length > 0
                    && char.IsHighSurrogate(text[length - 1])
                    && length < text.Length
                    && char.IsLowSurrogate(text[length]))
                {
                    length--;
                }

                text = text[..length];
            }

            return ValueTask.FromResult(new TerminalSelectionText(text, true, truncated));
        }
    }

    public unsafe ValueTask<TerminalFindResult> FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var query = Encoding.UTF8.GetBytes(input.Query);
            fixed (byte* queryPointer = query)
            {
                var options = GhosttyVtTerminalSearchOptions.CreateSized();
                options.Query = new GhosttyVtString(
                    (nint)queryPointer,
                    checked((nuint)query.Length));
                options.RequestedMatchIndex = input.RequestedMatchIndex;
                options.MaximumMatches = MaximumFindMatches;
                var result = GhosttyVtTerminalSearchResult.CreateSized();
                EnsureSuccess(
                    GhosttyVtNative.TerminalSearch(_terminal, &options, &result),
                    "search terminal history");
                MarkContentChangedUnsafe();
                if (result.MatchCount == 0)
                {
                    _selectionAnchor = null;
                    return ValueTask.FromResult(TerminalFindResult.Empty);
                }

                var matchCount = checked((int)result.MatchCount);
                var selectedIndex = checked((int)result.SelectedMatchIndex);
                _selectionAnchor = null;
                return ValueTask.FromResult(new TerminalFindResult(
                    matchCount,
                    selectedIndex,
                    result.ScanTruncated != 0));
            }
        }
    }

    private unsafe void ClearSelectionUnsafe()
    {
        GhosttyVtNative.TerminalSet(
            _terminal,
            GhosttyVtTerminalOption.Selection,
            null);
        _selectionAnchor = null;
    }

    private unsafe GhosttyVtGridRef GetViewportGridReferenceUnsafe(int column, int row)
    {
        var point = new GhosttyVtPoint
        {
            Tag = GhosttyVtPointTag.Viewport,
            Value = new GhosttyVtPointValue
            {
                Coordinate = new GhosttyVtPointCoordinate
                {
                    X = checked((ushort)column),
                    Y = checked((uint)row),
                },
            },
        };
        var reference = GhosttyVtGridRef.CreateSized();
        EnsureSuccess(
            GhosttyVtNative.TerminalGridRef(_terminal, point, &reference),
            "map a viewport cell");
        return reference;
    }

}
