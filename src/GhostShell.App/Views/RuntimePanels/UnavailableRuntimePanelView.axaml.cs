using Avalonia.Controls;
using Avalonia.Interactivity;

using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class UnavailableRuntimePanelView : UserControl
{
    public UnavailableRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(sender, e);
    }

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);
}
