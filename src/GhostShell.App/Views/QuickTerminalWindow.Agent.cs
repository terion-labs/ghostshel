using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GhostShell.App.ViewModels;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace GhostShell.App.Views;

public sealed partial class QuickTerminalWindow
{
    private void OnToggleAgentClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is QuickTerminalViewModel viewModel)
        {
            viewModel.ToggleAgentPanel();
            Dispatcher.UIThread.Post(UpdateNativeAgentMaterial);
        }
    }

    private void OnToggleAgentPinClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is QuickTerminalViewModel viewModel)
        {
            viewModel.ToggleAgentPanelPin();
            Dispatcher.UIThread.Post(UpdateNativeAgentMaterial);
        }
    }

    private async void OnSendAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.SendAgentPromptAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnAgentQuestionResponseKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter
            || e.KeyModifiers != AvaloniaKeyModifiers.None
            || DataContext is not QuickTerminalViewModel
            {
                AgentChat.CanSubmitQuestionResponse: true,
            } viewModel)
        {
            return;
        }

        e.Handled = true;
        await RunAgentActionAsync(
            viewModel,
            (agent, token) => agent.SubmitQuestionResponseAsync(token));
    }

    private async void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.SubmitQuestionResponseAsync(token));

    private async void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.DeclineQuestionAsync(token));

    private async void OnEnableAgentCapabilityAskClick(
        object? sender,
        RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.EnableCapabilityAskAsync(token));

    private async void OnKeepAgentCapabilityOffClick(
        object? sender,
        RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.KeepCapabilityOffAsync(token));

    private async void OnCancelAgentChatClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.StopAsync(token));

    private async void OnCancelAgentActionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.CancelActiveActionAsync(token));

    private async void OnClearAgentChatClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.ClearAsync(token));

    private async void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.DisableYoloAsync(token));

    private async void OnApproveAgentActionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.DecideAsync(approved: true, token));

    private async void OnDenyAgentActionClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.DecideAsync(approved: false, token));

    private async void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.RefreshAuditAsync(token));

    private async void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e) =>
        await RunAgentActionFromDataContextAsync(
            (agent, token) => agent.LoadOlderAuditAsync(token));

    private async void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not QuickTerminalViewModel
            {
                AgentChat: { CanEnableYolo: true } agentChat,
            } viewModel)
        {
            return;
        }

        var previousHideOnFocusLoss = HideOnFocusLoss;
        HideOnFocusLoss = false;
        TimeSpan? lifetime;
        try
        {
            lifetime = await new AgentYoloConfirmationDialog(
                    agentChat.TargetTitle,
                    agentChat.ExactTarget,
                    agentChat.ConnectionBoundary.Length > 0
                        ? agentChat.ConnectionBoundary
                        : "Not reported",
                    agentChat.WorkingDirectory.Length > 0
                        ? agentChat.WorkingDirectory
                        : "Not reported")
                .ShowDialog<TimeSpan?>(this);
        }
        finally
        {
            HideOnFocusLoss = previousHideOnFocusLoss;
        }

        if (lifetime is not null)
        {
            await RunAgentActionAsync(
                viewModel,
                (agent, token) => agent.EnableYoloAsync(lifetime.Value, token));
        }
    }

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        AgentSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAgentActionFromDataContextAsync(
        Func<AgentChatViewModel, CancellationToken, Task> action)
    {
        if (DataContext is QuickTerminalViewModel viewModel)
        {
            await RunAgentActionAsync(viewModel, action);
        }
    }

    private async Task RunAgentActionAsync(
        QuickTerminalViewModel viewModel,
        Func<AgentChatViewModel, CancellationToken, Task> action)
    {
        if (viewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await action(agentChat, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }
}
