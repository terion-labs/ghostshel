using System.Numerics;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using GhostShell.App;
using GhostShell.Core;

namespace GhostShell.App.Views;

public sealed partial class QuickTerminalWindow : Window
{
    private static readonly CubicEaseOut SlideEasing = new();
    private static readonly IReadOnlyList<WindowTransparencyLevel> TransparentHint =
        [WindowTransparencyLevel.Transparent];
    private static readonly IReadOnlyList<WindowTransparencyLevel> BlurHint =
    [
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.Blur,
        WindowTransparencyLevel.Transparent,
    ];
    private bool _allowClose;
    private QuickTerminalSettings _settings = QuickTerminalSettings.Default;
    private HostAccessibilityPreferences _hostPreferences =
        HostAccessibilityPreferences.Default;
    private double _preparedRevealProgress = 1;

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
        _settings = settings;
        _hostPreferences = hostPreferences;
        var backgroundOpacity = QuickTerminalPresentationPolicy.EffectiveOpacity(
            settings,
            hostPreferences);
        Opacity = 1;
        QuickTerminalHost.BackgroundOpacity = backgroundOpacity;
        QuickTerminalPlaceholderBackground.Opacity = backgroundOpacity;
        QuickTerminalStatusBackground.Opacity = backgroundOpacity;
        HideOnFocusLoss = settings.HideOnFocusLoss;
        var transparencyHint = QuickTerminalPresentationPolicy.ShouldUseBlur(
                settings,
                hostPreferences)
            ? OperatingSystem.IsMacOS()
                ? TransparentHint
                : BlurHint
            : TransparentHint;
        ApplyTransparencyHint(transparencyHint);
    }

    /// <summary>
    /// Applies the backdrop after the native window exists. macOS gets the
    /// configured radius; unsupported paths fall back to Avalonia's blur tier.
    /// </summary>
    public void ApplyBackdrop()
    {
        if (_hostPreferences.ReducedTransparency
            || !QuickTerminalPresentationPolicy.ShouldUseBlur(
                _settings,
                _hostPreferences))
        {
            if (OperatingSystem.IsMacOS())
            {
                _ = MacOsQuickTerminalBackdrop.TryApply(this, 0);
            }

            return;
        }

        if (OperatingSystem.IsMacOS()
            && MacOsQuickTerminalBackdrop.TryApply(this, _settings.BlurRadius))
        {
            return;
        }

        ApplyTransparencyHint(BlurHint);
    }

    private void ApplyTransparencyHint(IReadOnlyList<WindowTransparencyLevel> hint)
    {
        // Avalonia.Native 12.0.1 resets a repeated, already-satisfied hint to
        // opaque. Keep this setter idempotent so show/hide cycles cannot toggle
        // the native window between transparent and opaque modes.
        if (TransparencyLevelHint.SequenceEqual(hint))
        {
            return;
        }

        TransparencyLevelHint = hint;
    }

    public void PrepareReveal(double progress)
    {
        _preparedRevealProgress = Math.Clamp(progress, 0, 1);
        SlidingPanel.Opacity = _preparedRevealProgress >= 1 ? 1 : 0;
    }

    public void SetRevealProgress(double progress)
    {
        _preparedRevealProgress = Math.Clamp(progress, 0, 1);
        var visual = ElementComposition.GetElementVisual(SlidingPanel);
        if (visual is not null)
        {
            visual.StopAnimation("Translation");
            visual.Translation = TranslationFor(_preparedRevealProgress);
        }

        SlidingPanel.Opacity = _preparedRevealProgress > 0 ? 1 : 0;
    }

    public void AnimateReveal(double from, double to, TimeSpan duration)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        _preparedRevealProgress = to;
        var visual = ElementComposition.GetElementVisual(SlidingPanel);
        if (visual is null || duration <= TimeSpan.Zero)
        {
            SetRevealProgress(to);
            return;
        }

        visual.StopAnimation("Translation");
        visual.Translation = TranslationFor(from);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Translation";
        animation.Duration = duration;
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertKeyFrame(0, TranslationFor(from));
        animation.InsertKeyFrame(1, TranslationFor(to), SlideEasing);
        SlidingPanel.Opacity = 1;
        visual.StartAnimation("Translation", animation);
    }

    private Vector3 TranslationFor(double progress) => new(
        0,
        checked((float)(-Math.Max(1, Bounds.Height) * (1 - progress))),
        0);

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
