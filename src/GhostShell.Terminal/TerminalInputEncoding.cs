using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

internal static class TerminalInputEncoding
{
    public static string EncodeKey(TerminalKeyStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        var modifier = ModifierCode(stroke.Modifiers);
        var modified = stroke.Modifiers != TerminalKeyModifiers.None;
        return stroke.Key switch
        {
            TerminalKey.Enter => "\r",
            TerminalKey.Tab => stroke.Modifiers.HasFlag(TerminalKeyModifiers.Shift) ? "\u001b[Z" : "\t",
            TerminalKey.Backspace => "\u007f",
            TerminalKey.Escape => "\u001b",
            TerminalKey.Space when stroke.Modifiers.HasFlag(TerminalKeyModifiers.Control) => "\0",
            TerminalKey.Space => " ",
            TerminalKey.Up => Arrow('A', modified, modifier),
            TerminalKey.Down => Arrow('B', modified, modifier),
            TerminalKey.Right => Arrow('C', modified, modifier),
            TerminalKey.Left => Arrow('D', modified, modifier),
            TerminalKey.Home => Arrow('H', modified, modifier),
            TerminalKey.End => Arrow('F', modified, modifier),
            TerminalKey.PageUp => Tilde(5, modified, modifier),
            TerminalKey.PageDown => Tilde(6, modified, modifier),
            TerminalKey.Insert => Tilde(2, modified, modifier),
            TerminalKey.Delete => Tilde(3, modified, modifier),
            >= TerminalKey.F1 and <= TerminalKey.F4 => FunctionOneToFour(stroke.Key, modified, modifier),
            >= TerminalKey.F5 and <= TerminalKey.F20 => FunctionFiveToTwenty(stroke.Key, modified, modifier),
            _ => throw new ArgumentOutOfRangeException(nameof(stroke), stroke.Key, "Unknown terminal key."),
        };
    }

    public static string EncodeSgrMouse(TerminalMouseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var button = input.Button switch
        {
            TerminalMouseButton.Left => 0,
            TerminalMouseButton.Middle => 1,
            TerminalMouseButton.Right => 2,
            TerminalMouseButton.None => 3,
            TerminalMouseButton.WheelUp => 64,
            TerminalMouseButton.WheelDown => 65,
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Button, "Unknown mouse button."),
        };
        if (input.Kind is TerminalMouseEventKind.Move or TerminalMouseEventKind.Drag)
        {
            button += 32;
        }

        if (input.Modifiers.HasFlag(TerminalKeyModifiers.Shift))
        {
            button += 4;
        }

        if (input.Modifiers.HasFlag(TerminalKeyModifiers.Alt)
            || input.Modifiers.HasFlag(TerminalKeyModifiers.Meta))
        {
            button += 8;
        }

        if (input.Modifiers.HasFlag(TerminalKeyModifiers.Control))
        {
            button += 16;
        }

        var terminator = input.Kind == TerminalMouseEventKind.Up ? 'm' : 'M';
        return $"\u001b[<{button};{input.Column + 1};{input.Row + 1}{terminator}";
    }

    private static string Arrow(char suffix, bool modified, int modifier) =>
        modified ? $"\u001b[1;{modifier}{suffix}" : $"\u001b[{suffix}";

    private static string Tilde(int number, bool modified, int modifier) =>
        modified ? $"\u001b[{number};{modifier}~" : $"\u001b[{number}~";

    private static string FunctionOneToFour(TerminalKey key, bool modified, int modifier)
    {
        var suffix = (char)('P' + ((int)key - (int)TerminalKey.F1));
        return modified ? $"\u001b[1;{modifier}{suffix}" : $"\u001bO{suffix}";
    }

    private static string FunctionFiveToTwenty(TerminalKey key, bool modified, int modifier)
    {
        var number = key switch
        {
            TerminalKey.F5 => 15,
            TerminalKey.F6 => 17,
            TerminalKey.F7 => 18,
            TerminalKey.F8 => 19,
            TerminalKey.F9 => 20,
            TerminalKey.F10 => 21,
            TerminalKey.F11 => 23,
            TerminalKey.F12 => 24,
            TerminalKey.F13 => 25,
            TerminalKey.F14 => 26,
            TerminalKey.F15 => 28,
            TerminalKey.F16 => 29,
            TerminalKey.F17 => 31,
            TerminalKey.F18 => 32,
            TerminalKey.F19 => 33,
            TerminalKey.F20 => 34,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown function key."),
        };
        return Tilde(number, modified, modifier);
    }

    private static int ModifierCode(TerminalKeyModifiers modifiers)
    {
        var code = 1;
        if (modifiers.HasFlag(TerminalKeyModifiers.Shift))
        {
            code += 1;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.Alt)
            || modifiers.HasFlag(TerminalKeyModifiers.Meta))
        {
            code += 2;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.Control))
        {
            code += 4;
        }

        return code;
    }
}

internal static class TerminalPasteSafety
{
    public static bool RequiresConfirmation(
        TerminalPasteInput input,
        TerminalPasteSafetyPolicy policy,
        bool bracketedPasteEnabled)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.ContainsUnsafeContent
            && !input.ConfirmedUnsafe
            && policy switch
            {
                TerminalPasteSafetyPolicy.AllowUnsafe => false,
                TerminalPasteSafetyPolicy.ProtectUnsafe => !bracketedPasteEnabled,
                TerminalPasteSafetyPolicy.ProtectUnsafeIncludingBracketed => true,
                _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown paste policy."),
            };
    }
}
