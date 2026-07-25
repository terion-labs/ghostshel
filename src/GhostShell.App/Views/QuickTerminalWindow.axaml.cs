using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GhostShell.App;
using GhostShell.Core;

namespace GhostShell.App.Views;

public sealed partial class QuickTerminalWindow : Window
{
    private bool _allowClose;

    public QuickTerminalWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
    }

    public event EventHandler? DismissRequested;

    public event EventHandler? SettingsRequested;

    public bool HideOnFocusLoss { get; set; } = true;

    public void ApplySettings(QuickTerminalSettings settings) =>
        ApplySettings(settings, HostAccessibilityPreferences.Default);

    public void ApplySettings(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        Opacity = QuickTerminalPresentationPolicy.EffectiveOpacity(
            settings,
            hostPreferences);
        HideOnFocusLoss = settings.HideOnFocusLoss;
        TransparencyLevelHint = hostPreferences.ReducedTransparency
            ? [WindowTransparencyLevel.None]
            : QuickTerminalPresentationPolicy.ShouldUseBlur(settings, hostPreferences)
            ? [WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.Transparent];
    }

    public void FocusTerminal() =>
        Dispatcher.UIThread.Post(
            () => QuickTerminalHost.RequestInputFocus(),
            DispatcherPriority.Loaded);

    public bool TryCancelPendingPaste() => QuickTerminalHost.TryCancelPendingPaste();

    public bool TryCancelPendingInteraction() =>
        QuickTerminalHost.TryCancelPendingInteraction();

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        var action = QuickTerminalRuntimeRules.ResolveEscape(
            IsVisible,
            TryCancelPendingInteraction());
        if (action != QuickTerminalEscapeAction.Hide)
        {
            return;
        }

        RequestDismiss();
    }

    private void OnHideClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RequestDismiss();
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (QuickTerminalRuntimeRules.ShouldDismissForFocusLoss(
                IsVisible,
                HideOnFocusLoss))
        {
            RequestDismiss();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _ = sender;
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        RequestDismiss();
    }

    private void RequestDismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);
}
