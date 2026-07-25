using GhostShell.Core;

namespace GhostShell.Terminal.Tests;

public sealed class GhosttyNativeHostKeyMapperTests
{
    [Fact]
    public void Control_prefix_maps_to_the_durable_application_stroke()
    {
        var native = new NativeTerminalHostKeyEventV1(
            physicalKey: 11,
            codepoint: 'b',
            modifiers: 1U << 2,
            isRepeat: false);

        Assert.True(GhosttyNativeHostKeyMapper.TryMap(native, out var input));
        Assert.Equal(new KeyStroke("B", KeyModifiers.Control), input.Stroke);
        Assert.False(input.IsRepeat);
    }

    [Fact]
    public void Shifted_symbol_maps_semantically_without_a_phantom_shift_modifier()
    {
        var native = new NativeTerminalHostKeyEventV1(
            physicalKey: 23,
            codepoint: '%',
            modifiers: 1U << 0,
            isRepeat: true);

        Assert.True(GhosttyNativeHostKeyMapper.TryMap(native, out var input));
        Assert.Equal(new KeyStroke("%"), input.Stroke);
        Assert.True(input.IsRepeat);
    }

    [Theory]
    [InlineData(18, '!', 1U << 0, "1", KeyModifiers.Shift)]
    [InlineData(11, 0x222B, 1U << 1, "B", KeyModifiers.Alt)]
    [InlineData(41, ':', 1U << 0, "OEMSEMICOLON", KeyModifiers.Shift)]
    [InlineData(30, ']', 0, "OEMCLOSEBRACKETS", KeyModifiers.None)]
    [InlineData(12, ';', 0, "OEMSEMICOLON", KeyModifiers.None)]
    public void Native_keys_use_the_same_canonical_names_as_the_Avalonia_recorder(
        uint physicalKey,
        uint codepoint,
        uint modifiers,
        string expectedKey,
        KeyModifiers expectedModifiers)
    {
        var native = new NativeTerminalHostKeyEventV1(
            physicalKey,
            codepoint,
            modifiers,
            isRepeat: false);

        Assert.True(GhosttyNativeHostKeyMapper.TryMap(native, out var input));
        Assert.Equal(new KeyStroke(expectedKey, expectedModifiers), input.Stroke);
    }

    [Theory]
    [InlineData(0xF700, "ARROWUP")]
    [InlineData(0xF702, "ARROWLEFT")]
    [InlineData(0xF704, "F1")]
    [InlineData(0xF717, "F20")]
    [InlineData(0xF72C, "PAGEUP")]
    public void Native_function_keys_map_without_Avalonia(
        uint codepoint,
        string expected)
    {
        var native = new NativeTerminalHostKeyEventV1(
            physicalKey: 0,
            codepoint,
            modifiers: 0,
            isRepeat: false);

        Assert.True(GhosttyNativeHostKeyMapper.TryMap(native, out var input));
        Assert.Equal(expected, input.Stroke.Key);
    }

    [Fact]
    public void Unknown_versions_and_modifier_bits_pass_through()
    {
        var futureVersion = new NativeTerminalHostKeyEventV1(
            physicalKey: 11,
            codepoint: 'b',
            modifiers: 0,
            isRepeat: false,
            version: 2);
        var unknownModifier = new NativeTerminalHostKeyEventV1(
            physicalKey: 11,
            codepoint: 'b',
            modifiers: 1U << 6,
            isRepeat: false);

        Assert.False(GhosttyNativeHostKeyMapper.TryMap(futureVersion, out _));
        Assert.False(GhosttyNativeHostKeyMapper.TryMap(unknownModifier, out _));
    }
}
