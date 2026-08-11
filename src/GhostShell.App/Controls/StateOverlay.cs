using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using FluentIcons.Common;

namespace GhostShell.App.Controls;

/// <summary>
/// Which of the three states a surface can be in instead of showing content.
/// </summary>
internal enum StateOverlayKind
{
    /// <summary>There is nothing here, and that is a fact, not a fault.</summary>
    Empty,

    /// <summary>The content is on its way; the heading says what is happening.</summary>
    Loading,

    /// <summary>The content could not be produced; the action offers a way back.</summary>
    Error,
}

/// <summary>
/// A surface's not-content: empty, loading, or failed, each with a heading, a
/// sentence, and at most one action.
///
/// Every panel hand-built these — over thirty blocks across the shell, several
/// byte-identical between panels, none agreeing on icon size or width. A view
/// states which of the three it is in and what the sentence says; what each
/// state looks like is decided once, here.
/// </summary>
internal sealed class StateOverlay : ContentControl
{
    public static readonly StyledProperty<StateOverlayKind> KindProperty =
        AvaloniaProperty.Register<StateOverlay, StateOverlayKind>(nameof(Kind));

    public static readonly StyledProperty<Symbol> GlyphProperty =
        AvaloniaProperty.Register<StateOverlay, Symbol>(nameof(Glyph), Symbol.Info);

    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<StateOverlay, string?>(nameof(Heading));

    public static readonly StyledProperty<string?> BodyProperty =
        AvaloniaProperty.Register<StateOverlay, string?>(nameof(Body));

    /// <summary>The one action the state offers — "Retry", "Reconnect". Absent,
    /// no button is drawn. Richer actions go in Content instead.</summary>
    public static readonly StyledProperty<string?> ActionLabelProperty =
        AvaloniaProperty.Register<StateOverlay, string?>(nameof(ActionLabel));

    /// <summary>The action as a command, for views that bind one; the
    /// <see cref="ActionRequested"/> event serves the ones that handle clicks.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<StateOverlay, System.Windows.Input.ICommand?>(
            nameof(ActionCommand));

    private Button? _action;

    static StateOverlay()
    {
        KindProperty.Changed.AddClassHandler<StateOverlay>(
            (overlay, _) => overlay.UpdateKindClass());
    }

    public StateOverlay() => UpdateKindClass();

    /// <summary>Raised when the state's one action is pressed.</summary>
    public event EventHandler<RoutedEventArgs>? ActionRequested;

    public StateOverlayKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Symbol Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string? ActionLabel
    {
        get => GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public System.Windows.Input.ICommand? ActionCommand
    {
        get => GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(StateOverlay);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_action is not null)
        {
            _action.Click -= OnActionClick;
        }

        _action = e.NameScope.Find<Button>("PART_Action");
        if (_action is not null)
        {
            _action.Click += OnActionClick;
        }
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        ActionRequested?.Invoke(this, e);
    }

    private void UpdateKindClass()
    {
        PseudoClasses.Set(":loading", Kind == StateOverlayKind.Loading);
        PseudoClasses.Set(":error", Kind == StateOverlayKind.Error);
    }
}
