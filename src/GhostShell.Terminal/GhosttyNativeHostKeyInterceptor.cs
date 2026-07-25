using System.Runtime.InteropServices;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

internal sealed class GhosttyNativeHostKeyInterceptor : IDisposable
{
    private readonly GhosttyTerminalHandle _terminal;
    private readonly GhosttyNativeHostKeyCallbackRegistration _callbackRegistration;
    private Func<NativeRendererKeyInput, bool>? _interceptor;
    private int _disposed;

    private GhosttyNativeHostKeyInterceptor(
        GhosttyTerminalHandle terminal,
        Func<NativeRendererKeyInput, bool> interceptor)
    {
        _terminal = terminal;
        _interceptor = interceptor;
        _callbackRegistration = GhosttyNativeHostKeyCallbackRegistry.Register(Intercept);
        try
        {
            if (!GhosttyNativeMethods.TerminalSetHostKeyInterceptorV1(
                    terminal,
                    GhosttyNativeHostKeyCallbackRegistry.NativeCallback,
                    _callbackRegistration.Id))
            {
                throw new GhosttyNativeException(
                    "Unable to install the native terminal host-key interceptor.");
            }
        }
        catch
        {
            Volatile.Write(ref _interceptor, null);
            _callbackRegistration.Dispose();
            throw;
        }
    }

    public static GhosttyNativeHostKeyInterceptor? Attach(
        GhosttyTerminalHandle terminal,
        Func<NativeRendererKeyInput, bool>? interceptor)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return interceptor is null ? null : new(terminal, interceptor);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_terminal.IsClosed && !_terminal.IsInvalid)
            {
                _ = GhosttyNativeMethods.TerminalSetHostKeyInterceptorV1(
                    _terminal,
                    null,
                    0);
            }
        }
        finally
        {
            Volatile.Write(ref _interceptor, null);
            _callbackRegistration.Dispose();
        }
    }

    private bool Intercept(nint userdata, in NativeTerminalHostKeyEventV1 keyEvent)
    {
        _ = userdata;
        var interceptor = Volatile.Read(ref _interceptor);
        if (interceptor is null
            || !GhosttyNativeHostKeyMapper.TryMap(keyEvent, out var input))
        {
            return false;
        }

        try
        {
            return interceptor(input);
        }
        catch (Exception exception)
        {
            GhosttyNativeHostKeyCallbackRegistry.TraceFailureNoThrow(
                "The native terminal host-key interceptor failed: {0}",
                exception.GetType().Name);
            return false;
        }
    }
}

internal static class GhosttyNativeHostKeyMapper
{
    private const uint EventVersion = 1;
    private const uint ShiftModifier = 1U << 0;
    private const uint AltModifier = 1U << 1;
    private const uint ControlModifier = 1U << 2;
    private const uint MetaModifier = 1U << 3;
    private const uint AllModifiers = ShiftModifier | AltModifier | ControlModifier | MetaModifier;

    private const uint UpArrowFunctionKey = 0xF700;
    private const uint DownArrowFunctionKey = 0xF701;
    private const uint LeftArrowFunctionKey = 0xF702;
    private const uint RightArrowFunctionKey = 0xF703;
    private const uint FirstFunctionKey = 0xF704;
    private const uint LastFunctionKey = 0xF726;
    private const uint InsertFunctionKey = 0xF727;
    private const uint DeleteFunctionKey = 0xF728;
    private const uint HomeFunctionKey = 0xF729;
    private const uint BeginFunctionKey = 0xF72A;
    private const uint EndFunctionKey = 0xF72B;
    private const uint PageUpFunctionKey = 0xF72C;
    private const uint PageDownFunctionKey = 0xF72D;

    public static bool TryMap(
        NativeTerminalHostKeyEventV1 native,
        out NativeRendererKeyInput input)
    {
        input = default;
        if (native.Version != EventVersion
            || native.StructSize < (uint)Marshal.SizeOf<NativeTerminalHostKeyEventV1>()
            || (native.Modifiers & ~AllModifiers) != 0
            || !TryMapKey(native.Codepoint, native.PhysicalKey, out var key, out var semanticCharacter))
        {
            return false;
        }

        var modifiers = MapModifiers(native.Modifiers);
        if (semanticCharacter)
        {
            modifiers &= ~KeyModifiers.Shift;
        }

        input = new NativeRendererKeyInput(
            new KeyStroke(key, modifiers),
            native.IsRepeat != 0);
        return true;
    }

    private static KeyModifiers MapModifiers(uint modifiers)
    {
        var mapped = KeyModifiers.None;
        if ((modifiers & ControlModifier) != 0)
        {
            mapped |= KeyModifiers.Control;
        }

        if ((modifiers & AltModifier) != 0)
        {
            mapped |= KeyModifiers.Alt;
        }

        if ((modifiers & ShiftModifier) != 0)
        {
            mapped |= KeyModifiers.Shift;
        }

        if ((modifiers & MetaModifier) != 0)
        {
            mapped |= KeyModifiers.Meta;
        }

        return mapped;
    }

    private static bool TryMapKey(
        uint codepoint,
        uint physicalKey,
        out string key,
        out bool semanticCharacter)
    {
        semanticCharacter = false;
        key = codepoint switch
        {
            (uint)'\r' or (uint)'\n' => "ENTER",
            (uint)'\t' => "TAB",
            0x08 or 0x7F => "BACKSPACE",
            0x1B => "ESCAPE",
            UpArrowFunctionKey => "ARROWUP",
            DownArrowFunctionKey => "ARROWDOWN",
            LeftArrowFunctionKey => "ARROWLEFT",
            RightArrowFunctionKey => "ARROWRIGHT",
            InsertFunctionKey => "INSERT",
            DeleteFunctionKey => "DELETE",
            HomeFunctionKey or BeginFunctionKey => "HOME",
            EndFunctionKey => "END",
            PageUpFunctionKey => "PAGEUP",
            PageDownFunctionKey => "PAGEDOWN",
            >= FirstFunctionKey and <= LastFunctionKey =>
                $"F{codepoint - FirstFunctionKey + 1}",
            _ => string.Empty,
        };
        if (key.Length > 0)
        {
            return true;
        }

        if (Rune.TryCreate(codepoint, out var semanticRune)
            && !Rune.IsControl(semanticRune)
            && !Rune.IsWhiteSpace(semanticRune))
        {
            key = semanticRune.ToString();
            semanticCharacter = key is "%" or "\"" or "&" or "," or "[" or "+" or "-";
            if (semanticCharacter)
            {
                return true;
            }
        }

        if (codepoint == ' ' || physicalKey == 49)
        {
            key = "SPACE";
            return true;
        }

        // Avalonia's macOS backend derives Key from the current layout for ASCII
        // letters and OEM punctuation, then falls back to the physical QWERTY key.
        // Reproduce that order so shortcuts recorded in the Avalonia editor also
        // resolve while the native NSView owns focus. Numeric-row keys always use
        // their physical digit; KeySymbol still wins above for the supported
        // semantic punctuation such as Shift+5 => "%".
        return TryMapNumericPhysicalKey(physicalKey, out key)
            || TryMapLogicalCharacter(codepoint, out key)
            || TryMapPhysicalKey(physicalKey, out key);
    }

    private static bool TryMapNumericPhysicalKey(uint physicalKey, out string key)
    {
        key = physicalKey switch
        {
            18 or 83 => "1",
            19 or 84 => "2",
            20 or 85 => "3",
            21 or 86 => "4",
            23 or 87 => "5",
            22 or 88 => "6",
            26 or 89 => "7",
            28 or 91 => "8",
            25 or 92 => "9",
            29 or 82 => "0",
            65 => "DECIMAL",
            67 => "MULTIPLY",
            71 => "CLEAR",
            75 => "DIVIDE",
            81 => "OEMPLUS",
            _ => string.Empty,
        };
        return key.Length > 0;
    }

    private static bool TryMapLogicalCharacter(uint codepoint, out string key)
    {
        key = codepoint switch
        {
            >= 'A' and <= 'Z' => ((char)codepoint).ToString(),
            >= 'a' and <= 'z' => char.ToUpperInvariant((char)codepoint).ToString(),
            ';' or ':' => "OEMSEMICOLON",
            '=' => "OEMPLUS",
            '<' => "OEMCOMMA",
            '_' => "-",
            '.' or '>' => "OEMPERIOD",
            '/' or '?' => "OEMQUESTION",
            '`' or '~' => "OEM3",
            '{' => "OEM4",
            '\\' or '|' => "OEMPIPE",
            ']' or '}' => "OEMCLOSEBRACKETS",
            '\'' => "OEMQUOTES",
            _ => string.Empty,
        };
        return key.Length > 0;
    }

    private static bool TryMapPhysicalKey(uint physicalKey, out string key)
    {
        key = physicalKey switch
        {
            0 => "A",
            11 => "B",
            8 => "C",
            2 => "D",
            14 => "E",
            3 => "F",
            5 => "G",
            4 => "H",
            34 => "I",
            38 => "J",
            40 => "K",
            37 => "L",
            46 => "M",
            45 => "N",
            31 => "O",
            35 => "P",
            12 => "Q",
            15 => "R",
            1 => "S",
            17 => "T",
            32 => "U",
            9 => "V",
            13 => "W",
            7 => "X",
            16 => "Y",
            6 => "Z",
            10 or 94 => "OEMBACKSLASH",
            24 or 81 => "OEMPLUS",
            27 or 78 => "-",
            30 => "OEMCLOSEBRACKETS",
            33 => "OEM4",
            39 => "OEMQUOTES",
            41 => "OEMSEMICOLON",
            42 or 93 => "OEMPIPE",
            43 => "OEMCOMMA",
            44 => "OEMQUESTION",
            47 => "OEMPERIOD",
            50 => "OEM3",
            67 => "MULTIPLY",
            69 => "+",
            75 => "DIVIDE",
            71 => "CLEAR",
            36 or 76 => "ENTER",
            48 => "TAB",
            49 => "SPACE",
            51 => "BACKSPACE",
            53 => "ESCAPE",
            114 => "INSERT",
            115 => "HOME",
            116 => "PAGEUP",
            117 => "DELETE",
            119 => "END",
            121 => "PAGEDOWN",
            123 => "ARROWLEFT",
            124 => "ARROWRIGHT",
            125 => "ARROWDOWN",
            126 => "ARROWUP",
            122 => "F1",
            120 => "F2",
            99 => "F3",
            118 => "F4",
            96 => "F5",
            97 => "F6",
            98 => "F7",
            100 => "F8",
            101 => "F9",
            109 => "F10",
            103 => "F11",
            111 => "F12",
            105 => "F13",
            107 => "F14",
            113 => "F15",
            106 => "F16",
            64 => "F17",
            79 => "F18",
            80 => "F19",
            90 => "F20",
            _ => string.Empty,
        };
        return key.Length > 0;
    }
}
