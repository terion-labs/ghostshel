using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Terminal;

/// <summary>
/// Translates the immutable terminal keymap captured at launch into libghostty's
/// configuration grammar. The native shim starts from libghostty defaults and then
/// loads these bindings, so one launched session keeps exactly the selected shortcut snapshot.
/// </summary>
internal static class GhosttyTerminalKeymap
{
    internal static IReadOnlyList<string> CreateBindings(TerminalKeymapSnapshot keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);

        return keymap.Bindings
            .Where(binding =>
                (binding.Contexts & (CommandContext.Global | CommandContext.Terminal)) != 0)
            .Select(CreateBinding)
            .ToArray();
    }

    private static string CreateBinding(CommandBinding binding)
    {
        if (binding.Sequence.Count != 1)
        {
            throw new NotSupportedException(
                "A native terminal shortcut must contain exactly one key stroke.");
        }

        if (!TerminalKeyBindingRules.IsSupported(binding.Sequence[0]))
        {
            throw new NotSupportedException(
                $"Native terminal key '{binding.Sequence[0].Key}' is not supported by every desktop renderer.");
        }

        var trigger = CreateTrigger(binding.Sequence[0]);
        return $"{trigger}={CreateAction(binding.CommandId)}";
    }

    private static string CreateTrigger(KeyStroke stroke)
    {
        var parts = new List<string>(5);
        if ((stroke.Modifiers & KeyModifiers.Control) != 0)
        {
            parts.Add("ctrl");
        }

        if ((stroke.Modifiers & KeyModifiers.Alt) != 0)
        {
            parts.Add("alt");
        }

        if ((stroke.Modifiers & KeyModifiers.Shift) != 0)
        {
            parts.Add("shift");
        }

        if ((stroke.Modifiers & KeyModifiers.Meta) != 0)
        {
            parts.Add("super");
        }

        parts.Add(CreateKey(stroke.Key));
        return string.Join('+', parts);
    }

    private static string CreateKey(string key)
    {
        var special = key switch
        {
            "ARROWLEFT" => "left",
            "ARROWRIGHT" => "right",
            "ARROWUP" => "up",
            "ARROWDOWN" => "down",
            "ENTER" => "enter",
            "TAB" => "tab",
            "BACKSPACE" => "backspace",
            "ESCAPE" => "escape",
            "SPACE" => "space",
            "HOME" => "home",
            "END" => "end",
            "PAGEUP" => "page_up",
            "PAGEDOWN" => "page_down",
            "INSERT" => "insert",
            "DELETE" => "delete",
            "OEMSEMICOLON" or "OEM1" => "Semicolon",
            "OEMPLUS" => "Equal",
            "OEMCOMMA" => "Comma",
            "OEMMINUS" => "Minus",
            "OEMPERIOD" => "Period",
            "OEMQUESTION" or "OEM2" => "Slash",
            "OEMTILDE" or "OEM3" => "Backquote",
            "OEMOPENBRACKETS" or "OEM4" => "BracketLeft",
            "OEMPIPE" or "OEM5" => "Backslash",
            "OEMCLOSEBRACKETS" or "OEM6" => "BracketRight",
            "OEMQUOTES" or "OEM7" => "Quote",
            "OEMBACKSLASH" or "OEM102" => "Backslash",
            "+" => "plus",
            _ => null,
        };
        if (special is not null)
        {
            return special;
        }

        if (key.Length is >= 2 and <= 3
            && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 20)
        {
            return key.ToLowerInvariant();
        }

        var runes = key.EnumerateRunes().ToArray();
        if (runes.Length != 1 || Rune.IsControl(runes[0]) || runes[0].Value is '\r' or '\n')
        {
            throw new NotSupportedException($"Native terminal key '{key}' is not supported.");
        }

        return Rune.ToLowerInvariant(runes[0]).ToString();
    }

    private static string CreateAction(CommandId commandId)
    {
        if (commandId == BuiltInCommands.Copy)
        {
            return "copy_to_clipboard";
        }

        if (commandId == BuiltInCommands.Paste)
        {
            return "paste_from_clipboard";
        }

        if (commandId == BuiltInCommands.SelectAll)
        {
            return "select_all";
        }

        if (commandId == BuiltInCommands.Find)
        {
            return "start_search";
        }

        if (commandId == BuiltInCommands.IncreaseFontSize)
        {
            return "increase_font_size:1";
        }

        if (commandId == BuiltInCommands.DecreaseFontSize)
        {
            return "decrease_font_size:1";
        }

        if (commandId == BuiltInCommands.ResetFontSize)
        {
            return "reset_font_size";
        }

        if (commandId == BuiltInCommands.ClearScrollback)
        {
            return "clear_screen";
        }

        var text = commandId == BuiltInCommands.MoveWordLeft ? "\\x1bb"
            : commandId == BuiltInCommands.MoveWordRight ? "\\x1bf"
            : commandId == BuiltInCommands.DeleteWordBackward ? "\\x17"
            : commandId == BuiltInCommands.DeleteWordForward ? "\\x1bd"
            : commandId == BuiltInCommands.MoveToLineStart ? "\\x01"
            : commandId == BuiltInCommands.MoveToLineEnd ? "\\x05"
            : commandId == BuiltInCommands.SendInterrupt ? "\\x03"
            : commandId == BuiltInCommands.SendEndOfFile ? "\\x04"
            : commandId == BuiltInCommands.ClearScreen ? "\\x0c"
            : null;
        if (text is not null)
        {
            return $"text:{text}";
        }

        // Durable keymaps intentionally preserve unknown future command IDs across
        // downgrades. Consume an unsupported native binding instead of leaking its
        // shortcut into the remote shell or failing the whole terminal attachment.
        return "ignore";
    }
}
