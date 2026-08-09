using Avalonia.Input;
using Exclr8Cef.WebView;

namespace Exclr8Cef.WebView.Tests;

public class KeyMapTests
{
    [Theory]
    [InlineData(Key.Back, 0x08)]
    [InlineData(Key.Tab, 0x09)]
    [InlineData(Key.Return, 0x0D)]  // Key.Enter has the same enum value
    [InlineData(Key.Escape, 0x1B)]
    [InlineData(Key.Space, 0x20)]
    [InlineData(Key.PageUp, 0x21)]
    [InlineData(Key.PageDown, 0x22)]
    [InlineData(Key.End, 0x23)]
    [InlineData(Key.Home, 0x24)]
    [InlineData(Key.Left, 0x25)]
    [InlineData(Key.Up, 0x26)]
    [InlineData(Key.Right, 0x27)]
    [InlineData(Key.Down, 0x28)]
    [InlineData(Key.Insert, 0x2D)]
    [InlineData(Key.Delete, 0x2E)]
    public void NavigationKeysMapToWindowsVK(Key avalonia, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.AvaloniaToWindowsVK(avalonia));
    }

    [Theory]
    [InlineData(Key.D0, 0x30)]
    [InlineData(Key.D5, 0x35)]
    [InlineData(Key.D9, 0x39)]
    public void DigitKeysMapToWindowsVK(Key avalonia, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.AvaloniaToWindowsVK(avalonia));
    }

    [Theory]
    [InlineData(Key.A, 0x41)]
    [InlineData(Key.M, 0x4D)]
    [InlineData(Key.Z, 0x5A)]
    public void LetterKeysMapToWindowsVK(Key avalonia, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.AvaloniaToWindowsVK(avalonia));
    }

    [Theory]
    [InlineData(Key.NumPad0, 0x60)]
    [InlineData(Key.NumPad9, 0x69)]
    [InlineData(Key.Multiply, 0x6A)]
    [InlineData(Key.Add, 0x6B)]
    [InlineData(Key.Subtract, 0x6D)]
    [InlineData(Key.Divide, 0x6F)]
    public void NumpadKeysMapToWindowsVK(Key avalonia, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.AvaloniaToWindowsVK(avalonia));
    }

    [Theory]
    [InlineData(Key.F1, 0x70)]
    [InlineData(Key.F12, 0x7B)]
    [InlineData(Key.F24, 0x87)]
    public void FunctionKeysMapToWindowsVK(Key avalonia, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.AvaloniaToWindowsVK(avalonia));
    }

    [Theory]
    [InlineData(Key.LeftShift, 0xA0)]
    [InlineData(Key.RightShift, 0xA1)]
    [InlineData(Key.LeftCtrl, 0xA2)]
    [InlineData(Key.RightCtrl, 0xA3)]
    [InlineData(Key.LeftAlt, 0xA4)]
    [InlineData(Key.RightAlt, 0xA5)]
    public void ModifierKeysMapToWindowsVK(Key avalonia, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.AvaloniaToWindowsVK(avalonia));
    }

    [Theory]
    [InlineData(Key.OemSemicolon, 0xBA)]
    [InlineData(Key.OemPlus, 0xBB)]
    [InlineData(Key.OemComma, 0xBC)]
    [InlineData(Key.OemMinus, 0xBD)]
    [InlineData(Key.OemTilde, 0xC0)]
    [InlineData(Key.OemOpenBrackets, 0xDB)]
    [InlineData(Key.OemPipe, 0xDC)]
    [InlineData(Key.OemCloseBrackets, 0xDD)]
    [InlineData(Key.OemQuotes, 0xDE)]
    public void OemPunctuationKeysMapToWindowsVK(Key avalonia, int expectedVk)
    {
        Assert.Equal(expectedVk, KeyMap.AvaloniaToWindowsVK(avalonia));
    }

    [Theory]
    // Avalonia Key values that have no corresponding entry in the switch.
    // The fallback returns 0 so unmapped keys are inert (the previous code's
    // `(int)key` fallback produced spurious VK values).
    [InlineData(Key.None)]
    [InlineData(Key.MediaPlayPause)]
    [InlineData(Key.MediaStop)]
    [InlineData(Key.MediaNextTrack)]
    [InlineData(Key.LaunchMail)]
    [InlineData(Key.SelectMedia)]
    public void UnmappedKeysReturnZero(Key avalonia)
    {
        Assert.Equal(0, KeyMap.AvaloniaToWindowsVK(avalonia));
    }
}
