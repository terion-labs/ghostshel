using System.Text.Json.Serialization;

namespace GhostShell.Core;

public enum TerminalCursorStyle
{
    Block,
    Bar,
    Underline,
}

public sealed record TerminalProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    public TerminalProfile(
        TerminalProfileId id,
        string name,
        string fontFamily,
        double fontSize,
        double lineHeight,
        TerminalCursorStyle cursorStyle,
        bool cursorBlink,
        int scrollbackLines,
        TerminalPalette palette,
        KeymapProfileId keymapId,
        TerminalClipboardPolicy? clipboardPolicy = null,
        TerminalLinkPolicy linkPolicy = TerminalLinkPolicy.ConfirmBeforeOpen,
        bool imeEnabled = true,
        TerminalShellIntegrationMode shellIntegration = TerminalShellIntegrationMode.Detect,
        TerminalBellMode bellMode = TerminalBellMode.Visual,
        TerminalCompatibilityProfile compatibility = TerminalCompatibilityProfile.Ghostty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        ArgumentNullException.ThrowIfNull(palette);

        if (!double.IsFinite(fontSize) || fontSize is < 6 or > 96)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), fontSize, "Font size must be between 6 and 96 points.");
        }

        if (!double.IsFinite(lineHeight) || lineHeight is < 0.8 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(lineHeight), lineHeight, "Line height must be between 0.8 and 3.");
        }

        if (scrollbackLines is < 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scrollbackLines),
                scrollbackLines,
                "Scrollback must be between 0 and 10,000,000 lines.");
        }

        if (fontFamily.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("A terminal font family must fit on one configuration line.", nameof(fontFamily));
        }

        if (!Enum.IsDefined(cursorStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(cursorStyle), cursorStyle, "Unknown terminal cursor style.");
        }

        if (!Enum.IsDefined(linkPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(linkPolicy), linkPolicy, "Unknown terminal link policy.");
        }

        if (!Enum.IsDefined(shellIntegration))
        {
            throw new ArgumentOutOfRangeException(
                nameof(shellIntegration),
                shellIntegration,
                "Unknown shell-integration mode.");
        }

        if (!Enum.IsDefined(bellMode))
        {
            throw new ArgumentOutOfRangeException(nameof(bellMode), bellMode, "Unknown terminal bell mode.");
        }

        if (!Enum.IsDefined(compatibility))
        {
            throw new ArgumentOutOfRangeException(
                nameof(compatibility),
                compatibility,
                "Unknown terminal compatibility profile.");
        }

        Id = id;
        Name = name;
        FontFamily = fontFamily;
        FontSize = fontSize;
        LineHeight = lineHeight;
        CursorStyle = cursorStyle;
        CursorBlink = cursorBlink;
        ScrollbackLines = scrollbackLines;
        Palette = palette;
        KeymapId = keymapId;
        ClipboardPolicy = clipboardPolicy ?? TerminalClipboardPolicy.Default;
        LinkPolicy = linkPolicy;
        ImeEnabled = imeEnabled;
        ShellIntegration = shellIntegration;
        BellMode = bellMode;
        Compatibility = compatibility;
    }

    public static DefinitionKind Kind => DefinitionKind.TerminalProfile;

    public TerminalProfileId Id { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public string Name { get; }

    public string FontFamily { get; }

    public double FontSize { get; }

    public double LineHeight { get; }

    public TerminalCursorStyle CursorStyle { get; }

    public bool CursorBlink { get; }

    public int ScrollbackLines { get; }

    public TerminalPalette Palette { get; }

    public KeymapProfileId KeymapId { get; }

    public TerminalClipboardPolicy ClipboardPolicy { get; }

    public TerminalLinkPolicy LinkPolicy { get; }

    public bool ImeEnabled { get; }

    public TerminalShellIntegrationMode ShellIntegration { get; }

    public TerminalBellMode BellMode { get; }

    public TerminalCompatibilityProfile Compatibility { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);
}
