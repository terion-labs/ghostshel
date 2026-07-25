using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GhostShell.App.Views;

public sealed partial class FileDeleteDialog : Window
{
    public FileDeleteDialog()
    {
        Heading = "Delete item?";
        Detail = "The selected item will be removed.";
        EffectText = "GhostShell cannot undo this operation.";
        InitializeComponent();
        DataContext = this;
    }

    public FileDeleteDialog(string kind, string name, string provider, bool hasVersionHistory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Heading = $"Delete {kind}?";
        Detail = $"“{name}” will be removed from {provider}.";
        EffectText = hasVersionHistory
            ? "GhostShell cannot undo this operation. The provider advertises version history, so an administrator may be able to recover an older version."
            : "This provider does not advertise trash or version recovery. GhostShell cannot undo this permanent deletion.";
        InitializeComponent();
        DataContext = this;
    }

    public string Heading { get; }

    public string Detail { get; }

    public string EffectText { get; }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(true);
    }
}
