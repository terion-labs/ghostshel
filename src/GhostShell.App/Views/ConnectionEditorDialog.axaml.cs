using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public sealed partial class ConnectionEditorDialog : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConnectionEditorDialogPurpose _purpose;

    public ConnectionEditorDialog()
    {
        InitializeComponent();
    }

    public ConnectionEditorDialog(ConnectionEditorViewModel viewModel)
        : this(viewModel, ConnectionEditorDialogPurpose.Save)
    {
    }

    public ConnectionEditorDialog(
        ConnectionEditorViewModel viewModel,
        ConnectionEditorDialogPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _purpose = purpose;
        InitializeComponent();
        DataContext = viewModel;
        if (purpose == ConnectionEditorDialogPurpose.Connect)
        {
            if (this.FindControl<CheckBox>("SaveConnectionCheckBox") is { } saveConnection)
            {
                saveConnection.IsVisible = true;
            }

            if (this.FindControl<Button>("SubmitButton") is { } submit)
            {
                submit.Content = "Connect";
            }
        }
    }

    private ConnectionEditorViewModel ViewModel => DataContext as ConnectionEditorViewModel
        ?? throw new InvalidOperationException("The connection editor view model is unavailable.");

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
        if (ViewModel.HostKeyReview is not { } review)
        {
            return;
        }

        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await ViewModel.TrustHostKeyAsync(_lifetime.Token);
        }
    }

    private void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            HideValidationError();
            var request = ViewModel.CreateSaveRequest();
            if (_purpose == ConnectionEditorDialogPurpose.Connect)
            {
                var saveConnection =
                    this.FindControl<CheckBox>("SaveConnectionCheckBox")?.IsChecked == true;
                Close(new ConnectionEditorConnectRequest(
                    request.Profile,
                    saveConnection));
                return;
            }

            Close(request);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
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

public enum ConnectionEditorDialogPurpose
{
    Save,
    Connect,
}
