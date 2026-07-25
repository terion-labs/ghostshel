using Avalonia.Input;
using GhostShell.Application;
using CoreKeyStroke = GhostShell.Core.KeyStroke;

namespace GhostShell.App.Controls;

internal interface IManagedTerminalInputSink
{
    ValueTask SendTextAsync(string text, CancellationToken cancellationToken);

    ValueTask SendKeyAsync(TerminalKeyStroke keyStroke, CancellationToken cancellationToken);

    ValueTask SendMouseAsync(TerminalMouseInput mouseInput, CancellationToken cancellationToken);

    ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken);

    ValueTask<bool> ClearScrollbackAsync(CancellationToken cancellationToken);

    ValueTask<TerminalFindResult?> FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken);

    ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken);

    ValueTask<TerminalSelectionText> ReadSelectionAsync(CancellationToken cancellationToken);

    ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken);
}

internal static class ManagedTerminalInput
{
    public static bool TryMapSpecialKey(
        Key key,
        KeyModifiers modifiers,
        out TerminalKeyStroke? keyStroke)
    {
        var terminalKey = key switch
        {
            Key.Return or Key.Enter => TerminalKey.Enter,
            Key.Tab => TerminalKey.Tab,
            Key.Back => TerminalKey.Backspace,
            Key.Escape => TerminalKey.Escape,
            Key.Space => TerminalKey.Space,
            Key.Up => TerminalKey.Up,
            Key.Down => TerminalKey.Down,
            Key.Left => TerminalKey.Left,
            Key.Right => TerminalKey.Right,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            Key.Insert => TerminalKey.Insert,
            Key.Delete => TerminalKey.Delete,
            >= Key.F1 and <= Key.F20 =>
                (TerminalKey)((int)TerminalKey.F1 + ((int)key - (int)Key.F1)),
            _ => (TerminalKey?)null,
        };
        if (terminalKey is null)
        {
            keyStroke = null;
            return false;
        }

        keyStroke = new TerminalKeyStroke(terminalKey.Value, MapModifiers(modifiers));
        return true;
    }

    public static bool TryEncodeModifiedText(
        string? keySymbol,
        KeyModifiers modifiers,
        out string text)
    {
        text = string.Empty;
        var textModifiers = modifiers
            & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (string.IsNullOrEmpty(keySymbol)
            || textModifiers == KeyModifiers.None
            || (modifiers.HasFlag(KeyModifiers.Control)
                && modifiers.HasFlag(KeyModifiers.Alt))
            || keySymbol.EnumerateRunes().Count() != 1)
        {
            return false;
        }

        var encoded = keySymbol;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            var character = char.ToUpperInvariant(keySymbol[0]);
            encoded = character switch
            {
                >= 'A' and <= 'Z' => ((char)(character - 'A' + 1)).ToString(),
                '@' or ' ' => "\0",
                '[' => "\u001b",
                '\\' => "\u001c",
                ']' => "\u001d",
                '^' => "\u001e",
                '_' => "\u001f",
                '?' => "\u007f",
                _ => string.Empty,
            };
            if (encoded.Length == 0)
            {
                return false;
            }
        }

        if (modifiers.HasFlag(KeyModifiers.Alt)
            || modifiers.HasFlag(KeyModifiers.Meta))
        {
            encoded = "\u001b" + encoded;
        }

        text = encoded;
        return true;
    }

    public static bool TryMapReplayStroke(
        CoreKeyStroke stroke,
        out TerminalKeyStroke? keyStroke,
        out string text)
    {
        var terminalKey = stroke.Key switch
        {
            "ENTER" or "RETURN" => TerminalKey.Enter,
            "TAB" => TerminalKey.Tab,
            "BACK" or "BACKSPACE" => TerminalKey.Backspace,
            "ESC" or "ESCAPE" => TerminalKey.Escape,
            "SPACE" => TerminalKey.Space,
            "UP" or "ARROWUP" => TerminalKey.Up,
            "DOWN" or "ARROWDOWN" => TerminalKey.Down,
            "LEFT" or "ARROWLEFT" => TerminalKey.Left,
            "RIGHT" or "ARROWRIGHT" => TerminalKey.Right,
            "HOME" => TerminalKey.Home,
            "END" => TerminalKey.End,
            "PAGEUP" => TerminalKey.PageUp,
            "PAGEDOWN" => TerminalKey.PageDown,
            "INSERT" => TerminalKey.Insert,
            "DELETE" => TerminalKey.Delete,
            _ when TryMapFunctionKey(stroke.Key, out var functionKey) => functionKey,
            _ => (TerminalKey?)null,
        };
        if (terminalKey is not null)
        {
            keyStroke = new TerminalKeyStroke(
                terminalKey.Value,
                MapModifiers(stroke.Modifiers));
            text = string.Empty;
            return true;
        }

        keyStroke = null;
        if (stroke.Key.EnumerateRunes().Count() != 1)
        {
            text = string.Empty;
            return false;
        }

        var value = stroke.Key;
        if ((stroke.Modifiers & GhostShell.Core.KeyModifiers.Shift) == 0)
        {
            value = value.ToLowerInvariant();
        }

        var characterModifiers = stroke.Modifiers
            & (GhostShell.Core.KeyModifiers.Control
                | GhostShell.Core.KeyModifiers.Alt
                | GhostShell.Core.KeyModifiers.Meta);
        if ((characterModifiers & GhostShell.Core.KeyModifiers.Control) != 0)
        {
            var character = char.ToUpperInvariant(value[0]);
            value = character switch
            {
                >= 'A' and <= 'Z' => ((char)(character - 'A' + 1)).ToString(),
                '@' or ' ' => "\0",
                '[' => "\u001b",
                '\\' => "\u001c",
                ']' => "\u001d",
                '^' => "\u001e",
                '_' => "\u001f",
                '?' => "\u007f",
                _ => string.Empty,
            };
            if (value.Length == 0)
            {
                text = string.Empty;
                return false;
            }
        }

        if ((characterModifiers
                & (GhostShell.Core.KeyModifiers.Alt | GhostShell.Core.KeyModifiers.Meta)) != 0)
        {
            value = "\u001b" + value;
        }

        text = value;
        return true;
    }

    public static TerminalKeyModifiers MapModifiers(KeyModifiers modifiers)
    {
        var result = TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Shift)
            ? TerminalKeyModifiers.Shift
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Alt)
            ? TerminalKeyModifiers.Alt
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Control)
            ? TerminalKeyModifiers.Control
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(KeyModifiers.Meta)
            ? TerminalKeyModifiers.Meta
            : TerminalKeyModifiers.None;
        return result;
    }

    private static TerminalKeyModifiers MapModifiers(GhostShell.Core.KeyModifiers modifiers)
    {
        var result = TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(GhostShell.Core.KeyModifiers.Shift)
            ? TerminalKeyModifiers.Shift
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(GhostShell.Core.KeyModifiers.Alt)
            ? TerminalKeyModifiers.Alt
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(GhostShell.Core.KeyModifiers.Control)
            ? TerminalKeyModifiers.Control
            : TerminalKeyModifiers.None;
        result |= modifiers.HasFlag(GhostShell.Core.KeyModifiers.Meta)
            ? TerminalKeyModifiers.Meta
            : TerminalKeyModifiers.None;
        return result;
    }

    private static bool TryMapFunctionKey(string key, out TerminalKey terminalKey)
    {
        if (key.Length is >= 2 and <= 3
            && key[0] == 'F'
            && int.TryParse(
                key.AsSpan(1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            && number is >= 1 and <= 20)
        {
            terminalKey = (TerminalKey)((int)TerminalKey.F1 + number - 1);
            return true;
        }

        terminalKey = default;
        return false;
    }

    public static bool TryMapScrollShortcut(
        Key key,
        KeyModifiers modifiers,
        int pageLines,
        out TerminalViewportScrollInput? scrollInput)
    {
        if ((modifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta))
            != KeyModifiers.Shift)
        {
            scrollInput = null;
            return false;
        }

        var lines = key switch
        {
            Key.PageUp => -Math.Max(1, pageLines),
            Key.PageDown => Math.Max(1, pageLines),
            _ => 0,
        };
        scrollInput = lines == 0 ? null : new TerminalViewportScrollInput(lines);
        return scrollInput is not null;
    }
}
