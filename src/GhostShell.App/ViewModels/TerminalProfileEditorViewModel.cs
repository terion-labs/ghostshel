using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed record TerminalProfileEditorSaveRequest(
    TerminalProfile Profile,
    long ExpectedRevision);

public sealed record TerminalKeymapOption(
    KeymapProfileId Id,
    string Name,
    bool IsAvailable)
{
    public string Label => IsAvailable ? Name : $"{Name} (missing)";

    public override string ToString() => Label;
}

public sealed class TerminalProfileEditorViewModel : ObservableObject
{
    private readonly TerminalProfile _original;
    private string _fontFamily;
    private double _fontSize;
    private double _lineHeight;
    private int _scrollbackLines;
    private TerminalCursorStyle _cursorStyle;
    private bool _cursorBlink;
    private string _foreground;
    private string _background;
    private string _cursor;
    private string _selection;
    private TerminalClipboardAccess _clipboardRead;
    private TerminalClipboardAccess _clipboardWrite;
    private TerminalPasteSafetyPolicy _pasteSafety;
    private TerminalLinkPolicy _linkPolicy;
    private bool _imeEnabled;
    private TerminalShellIntegrationMode _shellIntegration;
    private TerminalBellMode _bellMode;
    private TerminalCompatibilityProfile _compatibility;
    private TerminalKeymapOption _selectedKeymap;

    public TerminalProfileEditorViewModel(
        TerminalProfile profile,
        long expectedRevision,
        IEnumerable<KeymapProfile>? keymaps = null)
    {
        _original = profile ?? throw new ArgumentNullException(nameof(profile));
        ExpectedRevision = expectedRevision;
        TerminalKeymaps = CreateTerminalKeymapOptions(profile, keymaps);
        _selectedKeymap = TerminalKeymaps.Single(option => option.Id == profile.KeymapId);
        _fontFamily = profile.FontFamily;
        _fontSize = profile.FontSize;
        _lineHeight = profile.LineHeight;
        _scrollbackLines = profile.ScrollbackLines;
        _cursorStyle = profile.CursorStyle;
        _cursorBlink = profile.CursorBlink;
        _foreground = profile.Palette.Foreground.ToString();
        _background = profile.Palette.Background.ToString();
        _cursor = profile.Palette.Cursor.ToString();
        _selection = profile.Palette.SelectionBackground.ToString();
        _clipboardRead = profile.ClipboardPolicy.ReadAccess;
        _clipboardWrite = profile.ClipboardPolicy.WriteAccess;
        _pasteSafety = profile.ClipboardPolicy.PasteSafety;
        _linkPolicy = profile.LinkPolicy;
        _imeEnabled = profile.ImeEnabled;
        _shellIntegration = profile.ShellIntegration;
        _bellMode = profile.BellMode;
        _compatibility = profile.Compatibility;
    }

    public long ExpectedRevision { get; }

    public TerminalProfileId ProfileId => _original.Id;

    public IReadOnlyList<TerminalCursorStyle> CursorStyles { get; } = Enum.GetValues<TerminalCursorStyle>();

    public IReadOnlyList<TerminalClipboardAccess> ClipboardAccessOptions { get; } = Enum.GetValues<TerminalClipboardAccess>();

    public IReadOnlyList<TerminalPasteSafetyPolicy> PasteSafetyOptions { get; } = Enum.GetValues<TerminalPasteSafetyPolicy>();

    public IReadOnlyList<TerminalLinkPolicy> LinkPolicies { get; } = Enum.GetValues<TerminalLinkPolicy>();

    public IReadOnlyList<TerminalShellIntegrationMode> ShellIntegrationModes { get; } = Enum.GetValues<TerminalShellIntegrationMode>();

    public IReadOnlyList<TerminalBellMode> BellModes { get; } = Enum.GetValues<TerminalBellMode>();

    public IReadOnlyList<TerminalCompatibilityProfile> CompatibilityProfiles { get; } = Enum.GetValues<TerminalCompatibilityProfile>();

    public IReadOnlyList<TerminalKeymapOption> TerminalKeymaps { get; }

    public string FontFamily { get => _fontFamily; set => SetProperty(ref _fontFamily, value); }

    public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, value); }

    public double LineHeight { get => _lineHeight; set => SetProperty(ref _lineHeight, value); }

    public int ScrollbackLines { get => _scrollbackLines; set => SetProperty(ref _scrollbackLines, value); }

    public TerminalCursorStyle CursorStyle { get => _cursorStyle; set => SetProperty(ref _cursorStyle, value); }

    public bool CursorBlink { get => _cursorBlink; set => SetProperty(ref _cursorBlink, value); }

    public string Foreground { get => _foreground; set => SetProperty(ref _foreground, value); }

    public string Background { get => _background; set => SetProperty(ref _background, value); }

    public string Cursor { get => _cursor; set => SetProperty(ref _cursor, value); }

    public string Selection { get => _selection; set => SetProperty(ref _selection, value); }

    public TerminalClipboardAccess ClipboardRead { get => _clipboardRead; set => SetProperty(ref _clipboardRead, value); }

    public TerminalClipboardAccess ClipboardWrite { get => _clipboardWrite; set => SetProperty(ref _clipboardWrite, value); }

    public TerminalPasteSafetyPolicy PasteSafety { get => _pasteSafety; set => SetProperty(ref _pasteSafety, value); }

    public TerminalLinkPolicy LinkPolicy { get => _linkPolicy; set => SetProperty(ref _linkPolicy, value); }

    public bool ImeEnabled { get => _imeEnabled; set => SetProperty(ref _imeEnabled, value); }

    public TerminalShellIntegrationMode ShellIntegration { get => _shellIntegration; set => SetProperty(ref _shellIntegration, value); }

    public TerminalBellMode BellMode { get => _bellMode; set => SetProperty(ref _bellMode, value); }

    public TerminalCompatibilityProfile Compatibility { get => _compatibility; set => SetProperty(ref _compatibility, value); }

    public TerminalKeymapOption SelectedKeymap
    {
        get => _selectedKeymap;
        set => SetProperty(
            ref _selectedKeymap,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public bool MatchesTerminalKeymaps(IEnumerable<KeymapProfile> keymaps)
    {
        ArgumentNullException.ThrowIfNull(keymaps);
        return TerminalKeymaps.SequenceEqual(CreateTerminalKeymapOptions(_original, keymaps));
    }

    public TerminalProfileEditorSaveRequest CreateSaveRequest()
    {
        var palette = new TerminalPalette(
            _original.Palette.Name,
            RgbColor.Parse(Foreground),
            RgbColor.Parse(Background),
            RgbColor.Parse(Cursor),
            RgbColor.Parse(Selection),
            _original.Palette.AnsiColors);
        return new TerminalProfileEditorSaveRequest(
            new TerminalProfile(
                _original.Id,
                _original.Name,
                FontFamily,
                FontSize,
                LineHeight,
                CursorStyle,
                CursorBlink,
                ScrollbackLines,
                palette,
                SelectedKeymap.Id,
                new TerminalClipboardPolicy(ClipboardRead, ClipboardWrite, PasteSafety),
                LinkPolicy,
                ImeEnabled,
                ShellIntegration,
                BellMode,
                Compatibility),
            ExpectedRevision);
    }

    private static IReadOnlyList<TerminalKeymapOption> CreateTerminalKeymapOptions(
        TerminalProfile profile,
        IEnumerable<KeymapProfile>? keymaps)
    {
        var available = (keymaps ?? BuiltInKeymaps.All)
            .Where(keymap => keymap.Layer == KeymapLayer.Terminal)
            .GroupBy(keymap => keymap.Id)
            .Select(group => group.First())
            .OrderBy(keymap => keymap.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(keymap => keymap.Id.Value, StringComparer.Ordinal)
            .Select(keymap => new TerminalKeymapOption(keymap.Id, keymap.Name, IsAvailable: true))
            .ToList();
        if (available.All(option => option.Id != profile.KeymapId))
        {
            available.Add(new TerminalKeymapOption(
                profile.KeymapId,
                profile.KeymapId.Value,
                IsAvailable: false));
        }

        return Array.AsReadOnly(available.ToArray());
    }
}
