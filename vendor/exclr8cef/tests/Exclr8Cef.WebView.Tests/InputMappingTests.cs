using Avalonia.Input;
using Exclr8Cef;
using Exclr8Cef.WebView;

namespace Exclr8Cef.WebView.Tests;

public class InputMappingTests
{
    // ---- MapModifiers --------------------------------------------------

    [Fact]
    public void MapModifiers_None()
    {
        Assert.Equal(Cef.CefModifiers.None, InputMapping.MapModifiers(KeyModifiers.None));
    }

    [Theory]
    [InlineData(KeyModifiers.Shift, Cef.CefModifiers.Shift)]
    [InlineData(KeyModifiers.Control, Cef.CefModifiers.Control)]
    [InlineData(KeyModifiers.Alt, Cef.CefModifiers.Alt)]
    [InlineData(KeyModifiers.Meta, Cef.CefModifiers.Command)]
    public void MapModifiers_SingleFlag(KeyModifiers input, Cef.CefModifiers expected)
    {
        Assert.Equal(expected, InputMapping.MapModifiers(input));
    }

    [Fact]
    public void MapModifiers_CombinedShiftControl()
    {
        var result = InputMapping.MapModifiers(KeyModifiers.Shift | KeyModifiers.Control);
        Assert.Equal(Cef.CefModifiers.Shift | Cef.CefModifiers.Control, result);
    }

    [Fact]
    public void MapModifiers_AllFour()
    {
        var all = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        var expected = Cef.CefModifiers.Shift | Cef.CefModifiers.Control |
                       Cef.CefModifiers.Alt | Cef.CefModifiers.Command;
        Assert.Equal(expected, InputMapping.MapModifiers(all));
    }

    // ---- MapPointerUpdateKind ------------------------------------------

    [Theory]
    [InlineData(PointerUpdateKind.LeftButtonPressed, Cef.CefMouseButton.Left)]
    [InlineData(PointerUpdateKind.LeftButtonReleased, Cef.CefMouseButton.Left)]
    [InlineData(PointerUpdateKind.MiddleButtonPressed, Cef.CefMouseButton.Middle)]
    [InlineData(PointerUpdateKind.MiddleButtonReleased, Cef.CefMouseButton.Middle)]
    [InlineData(PointerUpdateKind.RightButtonPressed, Cef.CefMouseButton.Right)]
    [InlineData(PointerUpdateKind.RightButtonReleased, Cef.CefMouseButton.Right)]
    [InlineData(PointerUpdateKind.Other, Cef.CefMouseButton.Left)] // fallback
    public void MapPointerUpdateKind_Cases(PointerUpdateKind input, Cef.CefMouseButton expected)
    {
        Assert.Equal(expected, InputMapping.MapPointerUpdateKind(input));
    }

    // ---- MapInitiatingButton -------------------------------------------

    [Theory]
    [InlineData(MouseButton.Left, Cef.CefMouseButton.Left)]
    [InlineData(MouseButton.Middle, Cef.CefMouseButton.Middle)]
    [InlineData(MouseButton.Right, Cef.CefMouseButton.Right)]
    [InlineData(MouseButton.None, Cef.CefMouseButton.Left)]    // fallback (e.g. for touch)
    [InlineData(MouseButton.XButton1, Cef.CefMouseButton.Left)] // fallback
    [InlineData(MouseButton.XButton2, Cef.CefMouseButton.Left)] // fallback
    public void MapInitiatingButton_Cases(MouseButton input, Cef.CefMouseButton expected)
    {
        Assert.Equal(expected, InputMapping.MapInitiatingButton(input));
    }
}
