using System.Text;

namespace GhostShell.Terminal;

/// <summary>
/// Separates OSC commands at the emulator boundary so security-sensitive commands can be
/// brokered explicitly. All non-intercepted output is returned byte-for-byte as UTF-16 text.
/// Partial sequences are retained across PTY reads and the retained payload is bounded.
/// </summary>
internal sealed class PortableTerminalOscParser
{
    internal const int MaximumPayloadCharacters = 64 * 1024;

    private readonly StringBuilder _plainText = new();
    private readonly StringBuilder _payload = new();
    private ParserState _state;
    private bool _overflowed;

    public void Process(
        ReadOnlySpan<char> input,
        Action<string> writeTerminal,
        Action<string> observeOsc,
        Action<string> handleClipboard)
    {
        ArgumentNullException.ThrowIfNull(writeTerminal);
        ArgumentNullException.ThrowIfNull(observeOsc);
        ArgumentNullException.ThrowIfNull(handleClipboard);

        foreach (var character in input)
        {
            switch (_state)
            {
                case ParserState.Text:
                    if (character == '\u001b')
                    {
                        FlushPlainText(writeTerminal);
                        _state = ParserState.Escape;
                    }
                    else if (character == '\u009d')
                    {
                        FlushPlainText(writeTerminal);
                        BeginOsc();
                    }
                    else
                    {
                        _plainText.Append(character);
                    }

                    break;

                case ParserState.Escape:
                    if (character == ']')
                    {
                        BeginOsc();
                    }
                    else
                    {
                        _plainText.Append('\u001b');
                        _plainText.Append(character);
                        _state = ParserState.Text;
                    }

                    break;

                case ParserState.Osc:
                    if (character == '\u0007' || character == '\u009c')
                    {
                        CompleteOsc(
                            character.ToString(),
                            writeTerminal,
                            observeOsc,
                            handleClipboard);
                    }
                    else if (character == '\u001b')
                    {
                        _state = ParserState.OscEscape;
                    }
                    else
                    {
                        AppendPayload(character);
                    }

                    break;

                case ParserState.OscEscape:
                    if (character == '\\')
                    {
                        CompleteOsc(
                            "\u001b\\",
                            writeTerminal,
                            observeOsc,
                            handleClipboard);
                    }
                    else
                    {
                        AppendPayload('\u001b');
                        AppendPayload(character);
                        _state = ParserState.Osc;
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (_state == ParserState.Text)
        {
            FlushPlainText(writeTerminal);
        }
    }

    private void BeginOsc()
    {
        _payload.Clear();
        _overflowed = false;
        _state = ParserState.Osc;
    }

    private void AppendPayload(char character)
    {
        if (_overflowed)
        {
            return;
        }

        if (_payload.Length >= MaximumPayloadCharacters)
        {
            _payload.Clear();
            _overflowed = true;
            return;
        }

        _payload.Append(character);
    }

    private void CompleteOsc(
        string terminator,
        Action<string> writeTerminal,
        Action<string> observeOsc,
        Action<string> handleClipboard)
    {
        if (!_overflowed)
        {
            var payload = _payload.ToString();
            observeOsc(payload);
            if (IsClipboardCommand(payload))
            {
                handleClipboard(payload);
            }
            else
            {
                writeTerminal("\u001b]" + payload + terminator);
            }
        }

        _payload.Clear();
        _overflowed = false;
        _state = ParserState.Text;
    }

    private void FlushPlainText(Action<string> writeTerminal)
    {
        if (_plainText.Length == 0)
        {
            return;
        }

        writeTerminal(_plainText.ToString());
        _plainText.Clear();
    }

    private static bool IsClipboardCommand(string payload)
    {
        var separator = payload.IndexOf(';');
        return string.Equals(
            separator < 0 ? payload : payload[..separator],
            "52",
            StringComparison.Ordinal);
    }

    private enum ParserState
    {
        Text,
        Escape,
        Osc,
        OscEscape,
    }
}
