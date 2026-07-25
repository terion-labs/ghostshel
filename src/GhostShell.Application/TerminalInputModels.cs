using GhostShell.Core;

namespace GhostShell.Application;

public enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Escape,
    Space,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
}

[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1 << 0,
    Alt = 1 << 1,
    Control = 1 << 2,
    Meta = 1 << 3,
}

public sealed record TerminalKeyStroke
{
    public TerminalKeyStroke(TerminalKey Key, TerminalKeyModifiers Modifiers = TerminalKeyModifiers.None)
    {
        if (!Enum.IsDefined(Key))
        {
            throw new ArgumentOutOfRangeException(nameof(Key));
        }

        if ((Modifiers & ~AllModifiers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Modifiers));
        }

        this.Key = Key;
        this.Modifiers = Modifiers;
    }

    public TerminalKey Key { get; }

    public TerminalKeyModifiers Modifiers { get; }

    private const TerminalKeyModifiers AllModifiers =
        TerminalKeyModifiers.Shift
        | TerminalKeyModifiers.Alt
        | TerminalKeyModifiers.Control
        | TerminalKeyModifiers.Meta;
}

public enum TerminalCharacterChordModifier
{
    Control,
    Alt,
}

/// <summary>
/// A bounded physical-style terminal character chord. Character casing is
/// canonical: the lowercase value names the key and does not imply Shift.
/// </summary>
public sealed record TerminalCharacterChord
{
    public TerminalCharacterChord(
        char Character,
        TerminalCharacterChordModifier Modifier)
    {
        if (Character is < 'a' or > 'z')
        {
            throw new ArgumentOutOfRangeException(
                nameof(Character),
                Character,
                "A terminal character chord requires one lowercase ASCII letter.");
        }

        if (!Enum.IsDefined(Modifier))
        {
            throw new ArgumentOutOfRangeException(nameof(Modifier));
        }

        this.Character = Character;
        this.Modifier = Modifier;
    }

    public char Character { get; }

    public TerminalCharacterChordModifier Modifier { get; }
}

public enum TerminalMouseButton
{
    None,
    Left,
    Middle,
    Right,
    WheelUp,
    WheelDown,
}

public enum TerminalMouseEventKind
{
    Down,
    Up,
    Move,
    Drag,
    WheelUp,
    WheelDown,
}

public sealed record TerminalMouseInput
{
    public TerminalMouseInput(
        TerminalMouseButton Button,
        TerminalMouseEventKind Kind,
        int Column,
        int Row,
        TerminalKeyModifiers Modifiers = TerminalKeyModifiers.None)
    {
        if (!Enum.IsDefined(Button))
        {
            throw new ArgumentOutOfRangeException(nameof(Button));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (!IsSupportedEvent(Button, Kind))
        {
            throw new ArgumentException(
                "The terminal mouse button and event kind do not form a supported event.",
                nameof(Kind));
        }

        if (Column is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Column));
        }

        if (Row is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Row));
        }

        if ((Modifiers & ~(TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control | TerminalKeyModifiers.Meta)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Modifiers));
        }

        this.Button = Button;
        this.Kind = Kind;
        this.Column = Column;
        this.Row = Row;
        this.Modifiers = Modifiers;
    }

    public TerminalMouseButton Button { get; }

    public TerminalMouseEventKind Kind { get; }

    public int Column { get; }

    public int Row { get; }

    public TerminalKeyModifiers Modifiers { get; }

    private static bool IsSupportedEvent(
        TerminalMouseButton button,
        TerminalMouseEventKind kind) =>
        (button, kind) switch
        {
            (TerminalMouseButton.None, TerminalMouseEventKind.Move) => true,
            (
                TerminalMouseButton.Left
                    or TerminalMouseButton.Middle
                    or TerminalMouseButton.Right,
                TerminalMouseEventKind.Down
                    or TerminalMouseEventKind.Up
                    or TerminalMouseEventKind.Drag) => true,
            (TerminalMouseButton.WheelUp, TerminalMouseEventKind.WheelUp) => true,
            (TerminalMouseButton.WheelDown, TerminalMouseEventKind.WheelDown) => true,
            _ => false,
        };
}

public sealed record TerminalPasteInput
{
    public const int MaximumCharacters = 4 * 1024 * 1024;

    public TerminalPasteInput(string Text, bool ConfirmedUnsafe = false)
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Length > MaximumCharacters)
        {
            throw new ArgumentException(
                $"A terminal paste cannot exceed {MaximumCharacters} characters.",
                nameof(Text));
        }

        this.Text = Text;
        this.ConfirmedUnsafe = ConfirmedUnsafe;
    }

    public string Text { get; }

    public bool ConfirmedUnsafe { get; }

    public bool ContainsUnsafeContent => Text.Any(character =>
        character is '\r' or '\n'
        || (char.IsControl(character) && character != '\t'));
}

public sealed record TerminalPasteResult(
    bool Sent,
    bool RequiresConfirmation,
    bool UsedBracketedPaste,
    string? Detail)
{
    public static TerminalPasteResult ConfirmationRequired(bool bracketed) => new(
        false,
        true,
        bracketed,
        "The paste contains multiple lines or control characters and requires confirmation.");

    public static TerminalPasteResult Completed(bool bracketed) => new(
        true,
        false,
        bracketed,
        null);
}

public sealed record TerminalKeyRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalKeyStroke KeyStroke);

public sealed record TerminalMouseRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalMouseInput MouseInput);

public sealed record TerminalPasteRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalPasteInput PasteInput);
