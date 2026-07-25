using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GhostShell.App.Views;

public sealed partial class OperationErrorDialog : Window
{
    public OperationErrorDialog()
        : this("The operation failed.")
    {
    }

    public OperationErrorDialog(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Message = message;
        InitializeComponent();
        DataContext = this;
    }

    public string Message { get; }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
