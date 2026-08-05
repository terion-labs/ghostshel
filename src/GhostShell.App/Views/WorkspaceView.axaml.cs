using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;

using GhostShell.App.Controls;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public sealed partial class WorkspaceView : UserControl
{
    private int _dockInitializationGeneration;
    private IRootDock? _initializedDockLayout;

    public WorkspaceView()
    {
        InitializeComponent();
        RuntimeDockControl.HostWindowFactory =
            static () => new RuntimePanelHostWindow();
        RuntimeDockControl.PropertyChanged += OnRuntimeDockControlPropertyChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ScheduleDockInitialization();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _dockInitializationGeneration++;
        base.OnDetachedFromVisualTree(e);
    }

    public event EventHandler<RoutedEventArgs>? ActivateTabRequested;

    public event EventHandler<RoutedEventArgs>? ApproveAgentActionRequested;

    public event EventHandler<KeyEventArgs>? AgentQuestionResponseKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? CancelAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? CancelFileTransferRequested;

    public event EventHandler<RoutedEventArgs>? ClearAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? CloseRuntimeTabRequested;

    public event EventHandler<RoutedEventArgs>? DeclineAgentQuestionRequested;

    public event EventHandler<RoutedEventArgs>? DenyAgentActionRequested;

    public event EventHandler<RoutedEventArgs>? DisableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentCapabilityAskRequested;

    public event EventHandler<RoutedEventArgs>? EnableAgentYoloRequested;

    public event EventHandler<RoutedEventArgs>? KeepAgentCapabilityOffRequested;

    public event EventHandler<RoutedEventArgs>? LoadOlderAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? OpenWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? CloseWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? RefreshAgentAuditRequested;

    public event EventHandler<RoutedEventArgs>? RetryFileTransferRequested;

    public event EventHandler<SavedConnectionLaunchViewModel>?
        SavedConnectionLaunchRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDragEnterRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDragLeaveRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDragOverRequested;

    public event EventHandler<PointerCaptureLostEventArgs>?
        RuntimeTabDragPointerCaptureLostRequested;

    public event EventHandler<PointerEventArgs>? RuntimeTabDragPointerMovedRequested;

    public event EventHandler<PointerPressedEventArgs>? RuntimeTabDragPointerPressedRequested;

    public event EventHandler<PointerReleasedEventArgs>? RuntimeTabDragPointerReleasedRequested;

    public event EventHandler<DragEventArgs>? RuntimeTabDropRequested;

    public event EventHandler<RoutedEventArgs>? SendAgentChatRequested;

    public event EventHandler<RoutedEventArgs>? ShowAgentSettingsRequested;

    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;

    public event EventHandler<RoutedEventArgs>? ShowLauncherRequested;

    public event EventHandler<RoutedEventArgs>? ShowNewItemRequested;

    public event EventHandler<RoutedEventArgs>? ShowNewPanelRequested;

    /// <summary>
    /// Which edge the user chose for a new panel. The view forwards the side; the
    /// shell places an empty panel there and lets it ask what to open.
    /// </summary>
    public event EventHandler<PanelSide>? AddPanelToSideRequested;

    public event EventHandler<RoutedEventArgs>? LockShellRequested;

    public event EventHandler<RoutedEventArgs>? ShowSettingsRequested;

    public event EventHandler<RoutedEventArgs>? SubmitAgentQuestionRequested;

    public event EventHandler<PointerPressedEventArgs>? TitleBarPointerPressedRequested;

    public event EventHandler<RoutedEventArgs>? ToggleAgentRequested;

    private void OnRuntimeDockControlPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.Property == DockControl.LayoutProperty)
        {
            ScheduleDockInitialization();
        }
    }

    private void ScheduleDockInitialization()
    {
        var generation = ++_dockInitializationGeneration;
        Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(
                () => InitializeCurrentDockLayout(generation),
                DispatcherPriority.Background),
            DispatcherPriority.Loaded);
    }

    private void InitializeCurrentDockLayout(int generation)
    {
        if (generation != _dockInitializationGeneration
            || VisualRoot is null
            || RuntimeDockControl.Layout is not IRootDock layout
            || ReferenceEquals(layout, _initializedDockLayout)
            || layout.Factory is null)
        {
            return;
        }

        // DockControl's automatic InitializeLayout path closes native windows
        // whenever the view is transiently detached. Recovery necessarily
        // crosses one such launcher-to-workspace transition. Initialize only
        // the final attached layout so a restored floating window is not
        // presented and then immediately removed from the serialized model.
        layout.Factory.InitLayout(layout);
        _initializedDockLayout = layout;
    }

    private void OnActivateTabClick(object? sender, RoutedEventArgs e) =>
        ActivateTabRequested?.Invoke(sender, e);

    private void OnAgentQuestionResponseKeyDown(object? sender, KeyEventArgs e) =>
        AgentQuestionResponseKeyDownRequested?.Invoke(sender, e);

    private void OnApproveAgentActionClick(object? sender, RoutedEventArgs e) =>
        ApproveAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentActionClick(object? sender, RoutedEventArgs e) =>
        CancelAgentActionRequested?.Invoke(sender, e);

    private void OnCancelAgentChatClick(object? sender, RoutedEventArgs e) =>
        CancelAgentChatRequested?.Invoke(sender, e);

    private void OnCancelFileTransferClick(object? sender, RoutedEventArgs e) =>
        CancelFileTransferRequested?.Invoke(sender, e);

    private void OnClearAgentChatClick(object? sender, RoutedEventArgs e) =>
        ClearAgentChatRequested?.Invoke(sender, e);

    private void OnCloseRuntimeTabClick(object? sender, RoutedEventArgs e) =>
        CloseRuntimeTabRequested?.Invoke(sender, e);

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

    private void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e) =>
        OpenWorkspaceRequested?.Invoke(sender, e);

    private void OnCloseWorkspaceClick(object? sender, RoutedEventArgs e) =>
        CloseWorkspaceRequested?.Invoke(sender, e);

    private void OnRefreshAgentAuditClick(object? sender, RoutedEventArgs e) =>
        RefreshAgentAuditRequested?.Invoke(sender, e);

    private void OnRetryFileTransferClick(object? sender, RoutedEventArgs e) =>
        RetryFileTransferRequested?.Invoke(sender, e);

    private void OnSavedConnectionLaunchRequested(
        object? sender,
        SavedConnectionLaunchViewModel launch) =>
        SavedConnectionLaunchRequested?.Invoke(sender, launch);

    private void OnRuntimeTabDragEnter(object? sender, DragEventArgs e) =>
        RuntimeTabDragEnterRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragLeave(object? sender, DragEventArgs e) =>
        RuntimeTabDragLeaveRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragOver(object? sender, DragEventArgs e) =>
        RuntimeTabDragOverRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e) =>
        RuntimeTabDragPointerCaptureLostRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerMoved(object? sender, PointerEventArgs e) =>
        RuntimeTabDragPointerMovedRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerPressed(
        object? sender,
        PointerPressedEventArgs e) =>
        RuntimeTabDragPointerPressedRequested?.Invoke(sender, e);

    private void OnRuntimeTabDragPointerReleased(
        object? sender,
        PointerReleasedEventArgs e) =>
        RuntimeTabDragPointerReleasedRequested?.Invoke(sender, e);

    private void OnRuntimeTabDrop(object? sender, DragEventArgs e) =>
        RuntimeTabDropRequested?.Invoke(sender, e);

    private void OnSendAgentChatClick(object? sender, RoutedEventArgs e) =>
        SendAgentChatRequested?.Invoke(sender, e);

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowAgentSettingsRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowLauncherClick(object? sender, RoutedEventArgs e) =>
        ShowLauncherRequested?.Invoke(sender, e);

    private void OnShowNewItemClick(object? sender, RoutedEventArgs e) =>
        ShowNewItemRequested?.Invoke(sender, e);

    private void OnShowNewPanelClick(object? sender, RoutedEventArgs e) =>
        ShowNewPanelRequested?.Invoke(sender, e);

    private void OnAddPanelLeftClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Left);
    }

    private void OnAddPanelRightClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Right);
    }

    private void OnAddPanelTopClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Top);
    }

    private void OnAddPanelBottomClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        AddPanelToSideRequested?.Invoke(sender, PanelSide.Bottom);
    }

    private void OnLockShellClick(object? sender, RoutedEventArgs e) =>
        LockShellRequested?.Invoke(sender, e);

    private void OnShowSettingsClick(object? sender, RoutedEventArgs e) =>
        ShowSettingsRequested?.Invoke(sender, e);

    private void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        SubmitAgentQuestionRequested?.Invoke(sender, e);

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) =>
        TitleBarPointerPressedRequested?.Invoke(sender, e);

    private void OnToggleFileTransferManagerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        FileTransferManager.IsVisible = !FileTransferManager.IsVisible;
        if (FileTransferManager.IsVisible)
        {
            FileTransferManagerCloseButton.Focus(NavigationMethod.Tab);
            return;
        }

        FileTransferManagerButton.Focus(NavigationMethod.Tab);
    }

    private void OnToggleAgentClick(object? sender, RoutedEventArgs e) =>
        ToggleAgentRequested?.Invoke(sender, e);
}
