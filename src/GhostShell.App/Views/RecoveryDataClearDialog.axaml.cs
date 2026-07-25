using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GhostShell.App.Views;

public sealed partial class RecoveryDataClearDialog : Window
{
    public RecoveryDataClearDialog()
        : this(
            "Clear saved crash recovery data?",
            "Recovery snapshots from previous runs will be permanently removed from this profile.",
            "Clear recovery data")
    {
    }

    public RecoveryDataClearDialog(
        string heading,
        string detail,
        string confirmLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);
        Heading = heading.Trim();
        Detail = detail.Trim();
        ConfirmLabel = confirmLabel.Trim();
        InitializeComponent();
        DataContext = this;
    }

    public string Heading { get; }

    public string Detail { get; }

    public string ConfirmLabel { get; }

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
