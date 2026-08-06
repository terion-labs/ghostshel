using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace GhostShell.App.Views;

/// <summary>
/// Puts the shell's glass behind a popup.
///
/// A flyout is not drawn inside the window on this platform — it is a window of
/// its own, and a translucent surface in it has nothing behind it to blur. So
/// the menus came out see-through rather than frosted: the fill was doing all
/// the work, with the desktop showing through it unchanged.
///
/// The hint is what makes the platform build a visual-effect view for a top
/// level at all, so it has to be asked for on the popup's own window, exactly
/// as the main window asks for it. Behind an opaque surface it costs nothing
/// and shows nothing, which is why it is not conditional on the theme.
/// </summary>
internal static class TranslucentPopup
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsEnabled",
            typeof(TranslucentPopup));

    static TranslucentPopup() =>
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);

    public static void SetIsEnabled(Control control, bool value) =>
        control.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(Control control) =>
        control.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(
        Control control,
        AvaloniaPropertyChangedEventArgs change)
    {
        if (change.GetNewValue<bool>())
        {
            control.AttachedToVisualTree += OnAttached;
        }
        else
        {
            control.AttachedToVisualTree -= OnAttached;
        }
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ = e;
        // The popup's window exists only once its content is in a tree, which is
        // why this waits rather than running when the property is set.
        if (sender is not Control control || TopLevel.GetTopLevel(control) is not { } popup)
        {
            return;
        }

        // The popup's own root paints a square behind the card it holds. With
        // an opaque theme brush on it that square is what shows at the corners,
        // outside the card's radius and in front of the glass — which is the
        // block that survived masking the effect view, because the effect view
        // was never what was drawing it.
        popup.Background = Brushes.Transparent;
        popup.TransparencyLevelHint =
        [
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent,
        ];

        // Avalonia pins its effect view to a long-deprecated light material, so
        // the same correction the window needs applies here — and the glass is
        // masked to the shape of the surface it sits behind, because the window
        // holding it is a square and its corners would stand outside the card.
        MacOsWindowMaterial.TrySit(
            popup,
            MacOsMaterial.UnderWindowBackground,
            (control as TemplatedControl)?.CornerRadius.TopLeft);
    }
}
