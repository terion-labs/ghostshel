using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace GhostShell.App.Views;

public sealed partial class MainWindow
{
    public void ToggleAgentPanel()
    {
        if (ViewModel.IsWorkspaceVisible && !ViewModel.HasOverlay)
        {
            ViewModel.ToggleAgentPanel();
        }
    }

    private async void OnSendAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is null)
        {
            return;
        }

        try
        {
            await ViewModel.SendAgentPromptAsync(_lifetime.Token);
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
            || ViewModel.AgentChat is not { CanSubmitQuestionResponse: true } agentChat)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await agentChat.SubmitQuestionResponseAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.SubmitQuestionResponseAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DeclineQuestionAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnEnableAgentCapabilityAskClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.EnableCapabilityAskAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnKeepAgentCapabilityOffClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.KeepCapabilityOffAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnCancelAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.StopAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnCancelAgentActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.CancelActiveActionAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnClearAgentChatClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.ClearAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { CanEnableYolo: true } agentChat)
        {
            return;
        }

        var lifetime = await new AgentYoloConfirmationDialog(
                agentChat.TargetTitle,
                agentChat.ExactTarget,
                agentChat.ConnectionBoundary.Length > 0
                    ? agentChat.ConnectionBoundary
                    : "Not reported",
                agentChat.WorkingDirectory.Length > 0
                    ? agentChat.WorkingDirectory
                    : "Not reported")
            .ShowDialog<TimeSpan?>(this);
        if (lifetime is null)
        {
            return;
        }

        try
        {
            await agentChat.EnableYoloAsync(lifetime.Value, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { CanDisableYolo: true } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DisableYoloAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnApproveAgentActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DecideAsync(approved: true, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnDenyAgentActionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.DecideAsync(approved: false, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.RefreshAuditAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.AgentChat is not { } agentChat)
        {
            return;
        }

        try
        {
            await agentChat.LoadOlderAuditAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnToggleAgentClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ToggleAgentPanel();
    }

    private async void OnToggleAgentPinClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await ViewModel.ToggleAgentPanelPinAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }
}
