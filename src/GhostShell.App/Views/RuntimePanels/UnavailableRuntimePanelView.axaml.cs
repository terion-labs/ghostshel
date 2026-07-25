using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class UnavailableRuntimePanelView : UserControl
{
    public UnavailableRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(sender, e);
    }
}
