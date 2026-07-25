using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GhostShell.App.Views;

public sealed partial class DefinitionDeleteDialog : Window
{
    public DefinitionDeleteDialog()
    {
        Heading = "Delete definition?";
        Detail = "This durable definition will be removed from this profile.";
        Notice = "Referenced definitions are protected and will not be deleted.";
        ConfirmLabel = "Delete";
        InitializeComponent();
        DataContext = this;
    }

    public DefinitionDeleteDialog(string definitionKind, string name)
        : this(
            $"Delete {definitionKind}?",
            $"“{name}” will be permanently removed from this profile.",
            "Referenced definitions are protected and will not be deleted.",
            "Delete")
    {
    }

    public DefinitionDeleteDialog(
        string heading,
        string detail,
        string notice,
        string confirmLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(notice);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);
        Heading = heading.Trim();
        Detail = detail.Trim();
        Notice = notice.Trim();
        ConfirmLabel = confirmLabel.Trim();
        InitializeComponent();
        DataContext = this;
    }

    public string Heading { get; }

    public string Detail { get; }

    public string Notice { get; }

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
