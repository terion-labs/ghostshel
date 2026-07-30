using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public sealed partial class FileProviderProfileEditorDialog : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    public FileProviderProfileEditorDialog()
    {
        InitializeComponent();
    }

    public FileProviderProfileEditorDialog(FileProviderProfileEditorViewModel viewModel)
        : this(viewModel, FileProviderProfileEditorDialogPurpose.Save)
    {
    }

    public FileProviderProfileEditorDialog(
        FileProviderProfileEditorViewModel viewModel,
        FileProviderProfileEditorDialogPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        if (purpose == FileProviderProfileEditorDialogPurpose.Connect
            && this.FindControl<Button>("SubmitButton") is { } submit)
        {
            submit.Content = "Save and connect";
        }
    }

    private FileProviderProfileEditorViewModel ViewModel =>
        DataContext as FileProviderProfileEditorViewModel
        ?? throw new InvalidOperationException("The file-provider editor is unavailable.");

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

    private async void OnTrustHostKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        HideValidationError();
        if (ViewModel.HostKeyReview is not { } review)
        {
            return;
        }

        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await ViewModel.TrustHostKeyAsync(review.Id, _lifetime.Token);
        }
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

public enum FileProviderProfileEditorDialogPurpose
{
    Save,
    Connect,
}
