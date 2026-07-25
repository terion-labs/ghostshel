using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GhostShell.App.Views;

public sealed partial class DiscardChangesDialog : Window
{
    public DiscardChangesDialog()
    {
        Heading = "Discard layout changes?";
        Detail = "The unsaved grid, panel geometry, order, and minimum-size changes will be lost.";
        InitializeComponent();
        DataContext = this;
    }

    public DiscardChangesDialog(string heading, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Heading = heading.Trim();
        Detail = detail.Trim();
        InitializeComponent();
        DataContext = this;
    }

    public string Heading { get; }

    public string Detail { get; }

    private void OnKeepEditingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(true);
    }
}
