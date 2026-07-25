using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GhostShell.App.Views;

public sealed partial class AgentWorkspaceView : UserControl
{
    public AgentWorkspaceView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? ApproveAgentActionRequested;

    public event EventHandler<KeyEventArgs>? AgentQuestionResponseKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? DeclineAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? DenyAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? DisableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentCapabilityAskRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? KeepAgentCapabilityOffRequested;

    public event EventHandler<RoutedEventArgs>? LoadOlderAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? SendAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? ShowAgentSettingsRequested;

    public event EventHandler<RoutedEventArgs>? SubmitAgentQuestionRequested;

    private static void OnAgentChatTranscriptScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer transcript || e.ExtentDelta.Y <= 0)
        {
            return;
        }

        var previousExtentHeight = transcript.Extent.Height - e.ExtentDelta.Y;
        var previousEndOffset = Math.Max(
            0,
            previousExtentHeight - transcript.Viewport.Height);
        if (transcript.Offset.Y < previousEndOffset - 12)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(transcript.ScrollToEnd);
    }

    private void OnAgentQuestionResponseKeyDown(object? sender, KeyEventArgs e) =>
        AgentQuestionResponseKeyDownRequested?.Invoke(sender, e);

    private void OnApproveAgentActionClick(object? sender, RoutedEventArgs e) =>
        ApproveAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentActionClick(object? sender, RoutedEventArgs e) =>
        CancelAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentChatClick(object? sender, RoutedEventArgs e) =>
        CancelAgentChatRequested?.Invoke(sender, e);

    private void OnClearAgentChatClick(object? sender, RoutedEventArgs e) =>
        ClearAgentChatRequested?.Invoke(sender, e);

    private void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        DeclineAgentQuestionRequested?.Invoke(sender, e);

    private void OnDenyAgentActionClick(object? sender, RoutedEventArgs e) =>
        DenyAgentActionRequested?.Invoke(sender, e);

    private void OnDisableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        DisableAgentYoloRequested?.Invoke(sender, e);

    private void OnEnableAgentCapabilityAskClick(object? sender, RoutedEventArgs e) =>
        EnableAgentCapabilityAskRequested?.Invoke(sender, e);

    private void OnEnableAgentYoloClick(object? sender, RoutedEventArgs e) =>
        EnableAgentYoloRequested?.Invoke(sender, e);

    private void OnKeepAgentCapabilityOffClick(object? sender, RoutedEventArgs e) =>
        KeepAgentCapabilityOffRequested?.Invoke(sender, e);

    private void OnLoadOlderAgentAuditClick(object? sender, RoutedEventArgs e) =>
        LoadOlderAgentAuditRequested?.Invoke(sender, e);

    private void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentAuditRequested?.Invoke(sender, e);

    private void OnSendAgentChatClick(object? sender, RoutedEventArgs e) =>
        SendAgentChatRequested?.Invoke(sender, e);

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowAgentSettingsRequested?.Invoke(sender, e);

    private void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        SubmitAgentQuestionRequested?.Invoke(sender, e);
}
