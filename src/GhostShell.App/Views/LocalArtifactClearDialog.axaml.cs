using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public sealed partial class LocalArtifactClearDialog : Window
{
    public LocalArtifactClearDialog()
    {
        Heading = "Clear selected app-managed files?";
        Detail = "The selected files will be permanently removed.";
        ConfirmAutomationName = "Confirm clearing selected app-managed files";
        InitializeComponent();
        DataContext = this;
    }

    public LocalArtifactClearDialog(LocalArtifactItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Heading = $"Clear {item.Title.ToLowerInvariant()}?";
        Detail =
            $"Current inventory: {item.FileCountLabel}, {item.SizeLabel}. "
            + "All eligible files present in this category when cleanup starts "
            + "will be permanently removed from GhostSHELL’s dedicated storage location.";
        ConfirmAutomationName = $"Confirm {item.ClearAutomationName.ToLowerInvariant()}";
        InitializeComponent();
        DataContext = this;
    }

    public string Heading { get; }

    public string Detail { get; }

    public string ConfirmAutomationName { get; }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
