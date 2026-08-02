using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.RuntimePanels;

public sealed partial class DatabaseRuntimePanelView : UserControl
{
    public DatabaseRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    private DatabaseRuntimePanelViewModel? Panel => DataContext as DatabaseRuntimePanelViewModel;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(sender, e);
    }

    private void OnSplitLeftRightClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        SplitRequested?.Invoke(sender, PanelSplitOrientation.LeftRight);
    }

    private void OnSplitTopBottomClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        SplitRequested?.Invoke(sender, PanelSplitOrientation.TopBottom);
    }

    private void OnConnectionStringKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Panel is { } panel)
        {
            e.Handled = true;
            _ = panel.ConnectAsync();
        }
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && (e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            && Panel is { } panel)
        {
            e.Handled = true;
            _ = panel.RunQueryAsync();
        }
    }

    private void OnTableClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: DatabaseTableItemViewModel table }
            && Panel is { } panel)
        {
            _ = panel.PreviewTableAsync(table);
        }
    }
}
