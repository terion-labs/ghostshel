using Avalonia.Controls;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views.SettingsPages;

public sealed partial class NetworkingSettingsPageView : UserControl
{
    public NetworkingSettingsPageView()
    {
        InitializeComponent();
    }

    private NetworkSettingsViewModel? ViewModel => DataContext as NetworkSettingsViewModel;

    private void OnAddProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel?.BeginCreateProfile();
    }

    private async void OnEditProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (ViewModel is { } viewModel
            && sender is Control { DataContext: NetworkConnectionProfileItemViewModel item })
        {
            await viewModel.BeginEditProfileAsync(item, CancellationToken.None);
        }
    }

    private async void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (ViewModel is { } viewModel
            && sender is Control { DataContext: NetworkConnectionProfileItemViewModel item })
        {
            await viewModel.DeleteProfileAsync(item, CancellationToken.None);
        }
    }

    private async void OnSaveProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.SaveProfileAsync(CancellationToken.None);
        }
    }

    private async void OnStoreCredentialClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.StoreCredentialAsync(CancellationToken.None);
        }
    }

    private async void OnTestProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.TestProfileAsync(CancellationToken.None);
        }
    }

    private async void OnCancelProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.CancelProfileEditAsync(CancellationToken.None);
        }
    }

    private async void OnSavePolicyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.SavePolicyAsync(CancellationToken.None);
        }
    }
}
