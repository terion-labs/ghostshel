using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public sealed partial class AiProviderProfileEditorDialog : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    public AiProviderProfileEditorDialog()
    {
        InitializeComponent();
    }

    public AiProviderProfileEditorDialog(AiProviderProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private AiProviderProfileEditorViewModel ViewModel =>
        DataContext as AiProviderProfileEditorViewModel
        ?? throw new InvalidOperationException("The AI-provider editor is unavailable.");

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        HideValidationError();
        await ViewModel.TestAsync(_lifetime.Token);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            HideValidationError();
            Close(ViewModel.CreateSaveRequest());
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            var error = this.FindControl<TextBlock>("ValidationError");
            if (error is not null)
            {
                error.Text = exception.Message;
                error.IsVisible = true;
            }
        }
    }

    private void HideValidationError()
    {
        var error = this.FindControl<TextBlock>("ValidationError");
        if (error is not null)
        {
            error.IsVisible = false;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
