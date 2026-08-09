using Avalonia.Input;

namespace Exclr8Cef.WebView;

/// <summary>
/// Pure functions translating Avalonia input enums to Exclr8CEF / CEF
/// equivalents. Extracted from <see cref="WebView"/> so they can be unit
/// tested without spinning up CEF.
/// </summary>
internal static class InputMapping
{
    public static Cef.CefModifiers MapModifiers(KeyModifiers km)
    {
        var flags = Cef.CefModifiers.None;
        if ((km & KeyModifiers.Shift) != 0) flags |= Cef.CefModifiers.Shift;
        if ((km & KeyModifiers.Control) != 0) flags |= Cef.CefModifiers.Control;
        if ((km & KeyModifiers.Alt) != 0) flags |= Cef.CefModifiers.Alt;
        if ((km & KeyModifiers.Meta) != 0) flags |= Cef.CefModifiers.Command;
        return flags;
    }

    /// <summary>
    /// Same as <see cref="MapModifiers(KeyModifiers)"/> but also folds in the
    /// "button held down" flags from the pointer's current properties. CEF
    /// uses these on move events to recognise drag-select; without them, a
    /// mouse-drag through text never extends the selection.
    /// </summary>
    public static Cef.CefModifiers MapModifiers(KeyModifiers km, PointerPointProperties props)
    {
        var flags = MapModifiers(km);
        if (props.IsLeftButtonPressed)   flags |= Cef.CefModifiers.LeftMouseButton;
        if (props.IsMiddleButtonPressed) flags |= Cef.CefModifiers.MiddleMouseButton;
        if (props.IsRightButtonPressed)  flags |= Cef.CefModifiers.RightMouseButton;
        return flags;
    }

    public static Cef.CefMouseButton MapPointerUpdateKind(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => Cef.CefMouseButton.Middle,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => Cef.CefMouseButton.Right,
        _ => Cef.CefMouseButton.Left,
    };

    public static Cef.CefMouseButton MapInitiatingButton(MouseButton btn) => btn switch
    {
        MouseButton.Right => Cef.CefMouseButton.Right,
        MouseButton.Middle => Cef.CefMouseButton.Middle,
        _ => Cef.CefMouseButton.Left,
    };
}
