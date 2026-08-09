using Avalonia.Input;

namespace Exclr8Cef.WebView;

/// <summary>
/// Maps Avalonia <see cref="Key"/> values to Windows virtual-key codes,
/// which is what CEF's <c>CefKeyEvent.windows_key_code</c> expects on
/// every platform.
/// </summary>
internal static class KeyMap
{
    public static int AvaloniaToWindowsVK(Key key) => key switch
    {
        // Mouse buttons (rarely sent as keys, but in the enum).
        Key.Cancel => 0x03,
        Key.Back => 0x08,
        Key.Tab => 0x09,
        Key.Clear => 0x0C,
        Key.Return or Key.Enter => 0x0D,
        Key.Pause => 0x13,
        Key.CapsLock => 0x14,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Select => 0x29,
        Key.Print => 0x2A,
        Key.Execute => 0x2B,
        Key.PrintScreen => 0x2C,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.Help => 0x2F,

        // 0-9
        Key.D0 => 0x30, Key.D1 => 0x31, Key.D2 => 0x32, Key.D3 => 0x33,
        Key.D4 => 0x34, Key.D5 => 0x35, Key.D6 => 0x36, Key.D7 => 0x37,
        Key.D8 => 0x38, Key.D9 => 0x39,

        // A-Z (0x41-0x5A)
        Key.A => 0x41, Key.B => 0x42, Key.C => 0x43, Key.D => 0x44,
        Key.E => 0x45, Key.F => 0x46, Key.G => 0x47, Key.H => 0x48,
        Key.I => 0x49, Key.J => 0x4A, Key.K => 0x4B, Key.L => 0x4C,
        Key.M => 0x4D, Key.N => 0x4E, Key.O => 0x4F, Key.P => 0x50,
        Key.Q => 0x51, Key.R => 0x52, Key.S => 0x53, Key.T => 0x54,
        Key.U => 0x55, Key.V => 0x56, Key.W => 0x57, Key.X => 0x58,
        Key.Y => 0x59, Key.Z => 0x5A,

        // Modifier keys (Windows / Command).
        Key.LWin => 0x5B,
        Key.RWin => 0x5C,
        Key.Apps => 0x5D,
        Key.Sleep => 0x5F,

        // Numpad.
        Key.NumPad0 => 0x60, Key.NumPad1 => 0x61, Key.NumPad2 => 0x62,
        Key.NumPad3 => 0x63, Key.NumPad4 => 0x64, Key.NumPad5 => 0x65,
        Key.NumPad6 => 0x66, Key.NumPad7 => 0x67, Key.NumPad8 => 0x68,
        Key.NumPad9 => 0x69,
        Key.Multiply => 0x6A,
        Key.Add => 0x6B,
        Key.Separator => 0x6C,
        Key.Subtract => 0x6D,
        Key.Decimal => 0x6E,
        Key.Divide => 0x6F,

        // Function keys.
        Key.F1 => 0x70, Key.F2 => 0x71, Key.F3 => 0x72, Key.F4 => 0x73,
        Key.F5 => 0x74, Key.F6 => 0x75, Key.F7 => 0x76, Key.F8 => 0x77,
        Key.F9 => 0x78, Key.F10 => 0x79, Key.F11 => 0x7A, Key.F12 => 0x7B,
        Key.F13 => 0x7C, Key.F14 => 0x7D, Key.F15 => 0x7E, Key.F16 => 0x7F,
        Key.F17 => 0x80, Key.F18 => 0x81, Key.F19 => 0x82, Key.F20 => 0x83,
        Key.F21 => 0x84, Key.F22 => 0x85, Key.F23 => 0x86, Key.F24 => 0x87,

        Key.NumLock => 0x90,
        Key.Scroll => 0x91,

        Key.LeftShift => 0xA0,
        Key.RightShift => 0xA1,
        Key.LeftCtrl => 0xA2,
        Key.RightCtrl => 0xA3,
        Key.LeftAlt => 0xA4,
        Key.RightAlt => 0xA5,

        // Browser keys.
        Key.BrowserBack => 0xA6,
        Key.BrowserForward => 0xA7,
        Key.BrowserRefresh => 0xA8,
        Key.BrowserStop => 0xA9,
        Key.BrowserSearch => 0xAA,
        Key.BrowserFavorites => 0xAB,
        Key.BrowserHome => 0xAC,

        Key.VolumeMute => 0xAD,
        Key.VolumeDown => 0xAE,
        Key.VolumeUp => 0xAF,

        // OEM punctuation.
        Key.OemSemicolon => 0xBA,    // ';:'
        Key.OemPlus => 0xBB,         // '=+'
        Key.OemComma => 0xBC,        // ',<'
        Key.OemMinus => 0xBD,        // '-_'
        Key.OemPeriod => 0xBE,       // '.>'
        Key.OemQuestion => 0xBF,     // '/?'
        Key.OemTilde => 0xC0,        // '`~'
        Key.OemOpenBrackets => 0xDB, // '[{'
        Key.OemPipe => 0xDC,         // '\|'
        Key.OemCloseBrackets => 0xDD,// ']}'
        Key.OemQuotes => 0xDE,       // '\'"'
        Key.OemBackslash => 0xE2,    // ISO physical backslash

        // Unmapped keys: Avalonia's Key enum is its own numbering and does NOT
        // align with Windows VK codes outside the explicit cases above. Return
        // 0 so unrecognized keys are inert rather than spurious VKs.
        _ => 0,
    };

    /// <summary>
    /// Avalonia <see cref="Key"/> → macOS Carbon HIToolbox keycode. Used to
    /// populate <c>CefKeyEvent.native_key_code</c> on macOS so Chromium's
    /// DOM-event mapping can identify the physical key (e.g. <c>code=Tab</c>
    /// rather than the wrong-key-code we'd get if we passed the Windows VK
    /// here — Carbon and Windows VK numbering disagree, e.g. VK_TAB=0x09
    /// collides with kVK_ANSI_V=0x09).
    /// </summary>
    public static int AvaloniaToMacKeyCode(Key key) => key switch
    {
        // Letters (kVK_ANSI_*)
        Key.A => 0x00, Key.S => 0x01, Key.D => 0x02, Key.F => 0x03,
        Key.H => 0x04, Key.G => 0x05, Key.Z => 0x06, Key.X => 0x07,
        Key.C => 0x08, Key.V => 0x09,                Key.B => 0x0B,
        Key.Q => 0x0C, Key.W => 0x0D, Key.E => 0x0E, Key.R => 0x0F,
        Key.Y => 0x10, Key.T => 0x11, Key.O => 0x1F, Key.U => 0x20,
        Key.I => 0x22, Key.P => 0x23, Key.L => 0x25, Key.J => 0x26,
        Key.K => 0x28, Key.N => 0x2D, Key.M => 0x2E,

        // Digits (top row, not numpad)
        Key.D1 => 0x12, Key.D2 => 0x13, Key.D3 => 0x14, Key.D4 => 0x15,
        Key.D5 => 0x17, Key.D6 => 0x16, Key.D7 => 0x1A, Key.D8 => 0x1C,
        Key.D9 => 0x19, Key.D0 => 0x1D,

        // OEM punctuation (US ANSI layout)
        Key.OemMinus        => 0x1B,
        Key.OemPlus         => 0x18,  // '=' physical key
        Key.OemOpenBrackets => 0x21,  // '['
        Key.OemCloseBrackets=> 0x1E,  // ']'
        Key.OemPipe         => 0x2A,  // '\'
        Key.OemSemicolon    => 0x29,
        Key.OemQuotes       => 0x27,
        Key.OemComma        => 0x2B,
        Key.OemPeriod       => 0x2F,
        Key.OemQuestion     => 0x2C,  // '/'
        Key.OemTilde        => 0x32,  // '`'

        // Editor / navigation
        Key.Return   => 0x24,  // also Key.Enter
        Key.Tab      => 0x30,
        Key.Space    => 0x31,
        Key.Back     => 0x33,
        Key.Escape   => 0x35,
        Key.CapsLock => 0x39,
        Key.Left     => 0x7B,
        Key.Right    => 0x7C,
        Key.Down     => 0x7D,
        Key.Up       => 0x7E,
        Key.Home     => 0x73,
        Key.End      => 0x77,
        Key.PageUp   => 0x74,
        Key.PageDown => 0x79,
        Key.Delete   => 0x75,

        // Function keys
        Key.F1 => 0x7A, Key.F2 => 0x78, Key.F3 => 0x63, Key.F4 => 0x76,
        Key.F5 => 0x60, Key.F6 => 0x61, Key.F7 => 0x62, Key.F8 => 0x64,
        Key.F9 => 0x65, Key.F10 => 0x6D, Key.F11 => 0x67, Key.F12 => 0x6F,

        // Numpad
        Key.NumPad0 => 0x52, Key.NumPad1 => 0x53, Key.NumPad2 => 0x54,
        Key.NumPad3 => 0x55, Key.NumPad4 => 0x56, Key.NumPad5 => 0x57,
        Key.NumPad6 => 0x58, Key.NumPad7 => 0x59, Key.NumPad8 => 0x5B,
        Key.NumPad9 => 0x5C,
        Key.Add      => 0x45,
        Key.Subtract => 0x4E,
        Key.Multiply => 0x43,
        Key.Divide   => 0x4B,
        Key.Decimal  => 0x41,

        // -1 means "no Carbon equivalent known" — caller passes through to
        // the shim, which leaves Chromium to fall back to other heuristics.
        _ => -1,
    };
}
