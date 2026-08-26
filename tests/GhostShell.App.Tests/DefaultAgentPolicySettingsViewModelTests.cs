using GhostShell.App.ViewModels;
using GhostShell.Application;

namespace GhostShell.App.Tests;

public sealed class DefaultAgentPolicySettingsViewModelTests
{
    [Fact]
    public async Task Missing_policy_store_is_presented_by_the_settings_owner()
    {
        var errors = new List<string>();
        using var viewModel = new DefaultAgentPolicySettingsViewModel(
            null,
            null,
            new ImmediateDispatcher(),
            errors.Add,
            () => { });

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(viewModel.CanSave);
        Assert.Equal(
            "Default AI configuration storage is unavailable.",
            errors.Single());
    }

    [Fact]
    public void Dispose_rejects_provider_refresh()
    {
        var viewModel = new DefaultAgentPolicySettingsViewModel(
            null,
            null,
            new ImmediateDispatcher(),
            _ => { },
            () => { });
        viewModel.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            viewModel.RefreshProviders([]));
    }

    private sealed class ImmediateDispatcher : IUiThreadDispatcher
    {
        public Task InvokeAsync(
            Action action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
