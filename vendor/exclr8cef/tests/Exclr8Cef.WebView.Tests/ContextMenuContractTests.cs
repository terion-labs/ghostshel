namespace Exclr8Cef.WebView.Tests;

public sealed class ContextMenuContractTests
{
    [Fact]
    public void NativeMenuPayloadPreservesPresentationState()
    {
        var items = Cef.ParseContextMenuItems(
            "100\t0\t1\t0\t0\tCopy\n" +
            "0\t3\t1\t0\t0\t\n" +
            "101\t1\t0\t1\t1\tSpelling check");

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal(100, item.CommandId);
                Assert.Equal("Copy", item.Label);
                Assert.Equal(ContextMenuItemKind.Command, item.Kind);
                Assert.True(item.IsEnabled);
                Assert.False(item.IsChecked);
                Assert.False(item.IsSeparator);
                Assert.Equal(0, item.Depth);
            },
            item => Assert.True(item.IsSeparator),
            item =>
            {
                Assert.Equal(ContextMenuItemKind.Check, item.Kind);
                Assert.False(item.IsEnabled);
                Assert.True(item.IsChecked);
                Assert.Equal(1, item.Depth);
            });
    }

    [Fact]
    public void EmptyNativeMenuPayloadProducesNoItems()
    {
        Assert.Empty(Cef.ParseContextMenuItems(""));
    }

    [Fact]
    public void NativeMenuPayloadPreservesSubmenuDepth()
    {
        var items = Cef.ParseContextMenuItems(
            "0\t4\t1\t0\t0\tSpelling\n" +
            "200\t0\t1\t0\t1\tSuggestion");

        Assert.Equal(ContextMenuItemKind.Submenu, items[0].Kind);
        Assert.Equal(0, items[0].Depth);
        Assert.Equal(ContextMenuItemKind.Command, items[1].Kind);
        Assert.Equal(1, items[1].Depth);
    }

    [Theory]
    [InlineData("&Back", "Back")]
    [InlineData("Save && Copy", "Save & Copy")]
    [InlineData("Перезавантажити", "Перезавантажити")]
    public void CefMnemonicMarkersAreNotShownAsLabelText(
        string cefLabel,
        string expected)
    {
        Assert.Equal(expected, WebView.NormalizeContextMenuLabel(cefLabel));
    }
}
