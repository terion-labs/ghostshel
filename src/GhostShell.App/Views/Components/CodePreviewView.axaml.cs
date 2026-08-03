using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace GhostShell.App.Views.Components;

/// <summary>
/// Read-only source preview with TextMate syntax highlighting. The grammar is
/// picked from the file name, the token theme follows the application's theme
/// variant, and files without a known grammar render as plain text.
/// </summary>
public sealed partial class CodePreviewView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodePreviewView, string?>(nameof(Text));

    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<CodePreviewView, string?>(nameof(FileName));

    private RegistryOptions? _registryOptions;
    private TextMate.Installation? _textMate;

    public CodePreviewView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? FileName
    {
        get => GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_textMate is null)
        {
            _registryOptions = new RegistryOptions(CurrentThemeName());
            _textMate = Editor.InstallTextMate(_registryOptions);
        }

        SyncDocument();
        SyncGrammar();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _textMate?.Dispose();
        _textMate = null;
        _registryOptions = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            SyncDocument();
        }
        else if (change.Property == FileNameProperty)
        {
            SyncGrammar();
        }
    }

    private void SyncDocument()
    {
        Editor.Document.Text = Text ?? string.Empty;
        // A new preview starts at the top; a stale scroll offset from the
        // previous file would show the middle of the next one.
        Editor.ScrollToHome();
    }

    private void SyncGrammar()
    {
        if (_textMate is null || _registryOptions is null)
        {
            return;
        }

        var language = ResolveLanguage(FileName);
        _textMate.SetGrammar(language is null
            ? null
            : _registryOptions.GetScopeByLanguageId(language.Id));
    }

    private Language? ResolveLanguage(string? fileName)
    {
        if (_registryOptions is null
            || SourcePreviewGrammar.ResolveExtension(fileName) is not { } extension)
        {
            return null;
        }

        return _registryOptions.GetLanguageByExtension(extension);
    }

    private void ApplyTheme()
    {
        if (_textMate is null || _registryOptions is null)
        {
            return;
        }

        _textMate.SetTheme(_registryOptions.LoadTheme(CurrentThemeName()));
    }

    private ThemeName CurrentThemeName() =>
        ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;
}
