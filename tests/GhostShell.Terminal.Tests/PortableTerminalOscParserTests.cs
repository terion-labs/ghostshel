namespace GhostShell.Terminal.Tests;

public sealed class PortableTerminalOscParserTests
{
    [Fact]
    public void Partial_osc_is_reassembled_and_clipboard_commands_are_intercepted()
    {
        var parser = new PortableTerminalOscParser();
        var terminal = new List<string>();
        var observed = new List<string>();
        var clipboard = new List<string>();

        parser.Process(
            "before\u001b]52;c;".AsSpan(),
            terminal.Add,
            observed.Add,
            clipboard.Add);
        parser.Process(
            "?\u0007after".AsSpan(),
            terminal.Add,
            observed.Add,
            clipboard.Add);

        Assert.Equal(["before", "after"], terminal);
        Assert.Equal("52;c;?", Assert.Single(observed));
        Assert.Equal("52;c;?", Assert.Single(clipboard));
    }

    [Fact]
    public void Oversized_osc_payload_is_discarded_without_retaining_or_forwarding_it()
    {
        var parser = new PortableTerminalOscParser();
        var terminal = new List<string>();
        var observed = new List<string>();
        var clipboard = new List<string>();
        var oversized = "\u001b]8;;https://example.test/"
            + new string('x', PortableTerminalOscParser.MaximumPayloadCharacters + 1)
            + "\u0007safe";

        parser.Process(oversized.AsSpan(), terminal.Add, observed.Add, clipboard.Add);

        Assert.Equal("safe", Assert.Single(terminal));
        Assert.Empty(observed);
        Assert.Empty(clipboard);
    }
}
