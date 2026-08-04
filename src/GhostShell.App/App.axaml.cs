using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

public sealed partial class App : Avalonia.Application
{
    private readonly MainWindowViewModel? _mainWindowViewModel;
    private readonly ApplicationStartupState? _startupState;
    private readonly IRecoveryCoordinator? _recoveryCoordinator;
    private readonly IDefinitionCatalog? _definitionCatalog;
    private readonly IDefinitionBundleStore? _definitionBundleStore;
    private readonly IDiagnosticsBundleExporter? _diagnosticsExporter;
    private readonly IDiagnosticsBundleRequestSource? _diagnosticsRequestSource;
    private readonly IDiagnosticsArtifactPresenter? _diagnosticsArtifactPresenter;
    private readonly IRecentSessionHistoryExporter? _recentSessionHistoryExporter;
    private readonly RecoveryDataControlViewModel? _recoveryDataControlViewModel;
    private readonly LocalArtifactControlViewModel? _localArtifactControlViewModel;
    private readonly QuickTerminalController? _quickTerminalController;
    private readonly IHostAccessibilityPreferencesSource? _hostAccessibilityPreferences;
    private readonly IScreenColorSampler? _screenColorSampler;
    private IPlatformSettings? _platformSettings;
    private AvaloniaHostAppearanceAdapter? _hostAppearance;
    private INotifyCollectionChanged? _windowCollection;

    private static readonly string[] AppearanceClasses =
    [
        "profile-macos-classic",
        "profile-macos-liquid-glass",
        "profile-windows11",
        "profile-gnome",
        "profile-kde",
        "profile-ghostshell",
        "profile-custom",
        "appearance-light",
        "appearance-dark",
        "high-contrast",
        "motion-disabled",
        "materials-enabled",
    ];

    private static readonly int[] ScalableFontSizes =
    [
        8, 9, 10, 11, 12, 13, 14,
        15, 16, 17, 18, 19, 20, 21,
        22, 23, 24, 25, 26, 27, 28,
    ];

    public App()
    {
    }

    public App(
        MainWindowViewModel mainWindowViewModel,
        ApplicationStartupState startupState,
        IRecoveryCoordinator recoveryCoordinator,
        IDefinitionCatalog definitionCatalog,
        IDefinitionBundleStore definitionBundleStore,
        IDiagnosticsBundleExporter diagnosticsExporter,
        IDiagnosticsBundleRequestSource diagnosticsRequestSource,
        IDiagnosticsArtifactPresenter diagnosticsArtifactPresenter,
        IRecentSessionHistoryExporter recentSessionHistoryExporter,
        RecoveryDataControlViewModel recoveryDataControlViewModel,
        LocalArtifactControlViewModel localArtifactControlViewModel,
        QuickTerminalController quickTerminalController,
        IHostAccessibilityPreferencesSource hostAccessibilityPreferences,
        IScreenColorSampler screenColorSampler)
    {
        ArgumentNullException.ThrowIfNull(mainWindowViewModel);
        ArgumentNullException.ThrowIfNull(startupState);
        ArgumentNullException.ThrowIfNull(recoveryCoordinator);
        ArgumentNullException.ThrowIfNull(definitionCatalog);
        ArgumentNullException.ThrowIfNull(definitionBundleStore);
        ArgumentNullException.ThrowIfNull(diagnosticsExporter);
        ArgumentNullException.ThrowIfNull(diagnosticsRequestSource);
        ArgumentNullException.ThrowIfNull(diagnosticsArtifactPresenter);
        ArgumentNullException.ThrowIfNull(recentSessionHistoryExporter);
        ArgumentNullException.ThrowIfNull(recoveryDataControlViewModel);
        ArgumentNullException.ThrowIfNull(localArtifactControlViewModel);
        ArgumentNullException.ThrowIfNull(quickTerminalController);
        ArgumentNullException.ThrowIfNull(hostAccessibilityPreferences);
        ArgumentNullException.ThrowIfNull(screenColorSampler);
        _mainWindowViewModel = mainWindowViewModel;
        _startupState = startupState;
        _recoveryCoordinator = recoveryCoordinator;
        _definitionCatalog = definitionCatalog;
        _definitionBundleStore = definitionBundleStore;
        _diagnosticsExporter = diagnosticsExporter;
        _diagnosticsRequestSource = diagnosticsRequestSource;
        _diagnosticsArtifactPresenter = diagnosticsArtifactPresenter;
        _recentSessionHistoryExporter = recentSessionHistoryExporter;
        _recoveryDataControlViewModel = recoveryDataControlViewModel;
        _localArtifactControlViewModel = localArtifactControlViewModel;
        _quickTerminalController = quickTerminalController;
        _hostAccessibilityPreferences = hostAccessibilityPreferences;
        _screenColorSampler = screenColorSampler;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindowViewModel = _mainWindowViewModel
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the main window view model.");
            var mainWindow = new MainWindow(
                _definitionBundleStore
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide the definition bundle store."),
                _definitionCatalog
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide the definition catalog."),
                _diagnosticsExporter
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide the diagnostics exporter."),
                _diagnosticsRequestSource
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide the diagnostics request source."),
                _diagnosticsArtifactPresenter
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide the diagnostics artifact presenter."),
                _recentSessionHistoryExporter
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide the session-history exporter."),
                _recoveryDataControlViewModel
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide recovery data controls."),
                _localArtifactControlViewModel
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide app-managed storage controls."),
                _screenColorSampler
                    ?? throw new InvalidOperationException(
                        "The desktop composition root did not provide a screen colour sampler."))
            {
                DataContext = mainWindowViewModel,
            };
            desktop.MainWindow = mainWindow;
            foreach (var hostWindow in desktop.Windows.OfType<RuntimePanelHostWindow>())
            {
                hostWindow.RefreshRuntimePanelTemplates();
            }

            QuickTerminalController.Initialize(mainWindow);
            mainWindow.Closed += OnMainWindowClosed;
            desktop.Exit += OnDesktopExit;
            AttachAppearance(mainWindow);
            mainWindow.Opened += OnStartupWindowOpened;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow MainWindow =>
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow
        ?? throw new InvalidOperationException("The GhostSHELL window is unavailable.");

    private MainWindowViewModel MainWindowViewModel => _mainWindowViewModel
        ?? throw new InvalidOperationException("The main window view model is unavailable.");

    private QuickTerminalController QuickTerminalController => _quickTerminalController
        ?? throw new InvalidOperationException("The Quick Terminal controller is unavailable.");

    private void OnCommandPaletteMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        MainWindow.ShowCommandPalette();
    }

    private void OnSettingsMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        MainWindow.NavigateToSettings();
    }

    private void OnLauncherMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        MainWindow.NavigateToLauncher();
    }

    private void OnQuickTerminalMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QuickTerminalController.Toggle();
    }

    private async void OnNewTerminalMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await MainWindow.RequestNewTerminalAsync();
    }

    private async void OnAddPanelMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await MainWindow.ShowNewPanelChooserAsync();
    }

    private async void OnLayoutDesignerMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await MainWindow.ShowLayoutDesignerAsync();
    }

    private void OnToggleAgentMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        MainWindow.ToggleAgentPanel();
    }

    private async void OnPreviousTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await MainWindow.SelectRelativeTabAsync(-1);
    }

    private async void OnNextTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await MainWindow.SelectRelativeTabAsync(1);
    }

    private async void OnClosePanelMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await MainWindow.RequestClosePanelAsync();
    }

    private async void OnCloseTabMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await MainWindow.RequestCloseTabAsync();
    }

    private void AttachAppearance(MainWindow mainWindow)
    {
        _platformSettings = PlatformSettings
            ?? throw new InvalidOperationException("Platform appearance settings are unavailable.");
        var hostAccessibilityPreferences = _hostAccessibilityPreferences
            ?? throw new InvalidOperationException(
                "The desktop composition root did not provide host accessibility preferences.");
        _hostAppearance = new AvaloniaHostAppearanceAdapter(
            _platformSettings,
            hostAccessibilityPreferences);
        _platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
        hostAccessibilityPreferences.Changed += OnHostAccessibilityPreferencesChanged;
        hostAccessibilityPreferences.Start();
        ApplyAppearance();
        if (_definitionCatalog is not null)
        {
            _definitionCatalog.Changed += OnDefinitionCatalogChanged;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.Windows is INotifyCollectionChanged windowCollection)
        {
            _windowCollection = windowCollection;
            _windowCollection.CollectionChanged += OnWindowCollectionChanged;
        }

        mainWindow.Closed += (_, _) =>
        {
            if (_platformSettings is not null)
            {
                _platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
            }
            hostAccessibilityPreferences.Changed -= OnHostAccessibilityPreferencesChanged;
            if (_definitionCatalog is not null)
            {
                _definitionCatalog.Changed -= OnDefinitionCatalogChanged;
            }
            if (_windowCollection is not null)
            {
                _windowCollection.CollectionChanged -= OnWindowCollectionChanged;
                _windowCollection = null;
            }
        };
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues values)
    {
        _ = sender;
        _ = values;
        ApplyAppearance();
    }

    private void OnDefinitionCatalogChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyAppearance();
        });
    }

    private void OnHostAccessibilityPreferencesChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(ApplyAppearance);
    }

    private void OnWindowCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        if (e.NewItems is null)
        {
            return;
        }

        var resources = ResolveAppearanceResources();
        foreach (var window in e.NewItems.OfType<Window>())
        {
            ApplyToWindow(window, resources);
        }
    }

    private void ApplyAppearance()
    {
        var resources = ResolveAppearanceResources();
        RequestedThemeVariant = resources.ThemeVariant;
        ApplyApplicationResources(resources);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                ApplyToWindow(window, resources);
            }

            (desktop.MainWindow as MainWindow)?.RefreshAppearanceControlsFromStoredProfile();
        }
    }

    private EffectiveAppearanceResources ResolveAppearanceResources()
    {
        var preference = _definitionCatalog?.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)?.Value
            ?? ThemePreference.Default;
        var hostAppearance = _hostAppearance?.GetCurrent()
            ?? throw new InvalidOperationException("Platform appearance settings are unavailable.");
        return EffectiveAppearanceResourceMapper.Map(preference.Resolve(hostAppearance));
    }

    private void ApplyApplicationResources(EffectiveAppearanceResources resources)
    {
        Resources["ShellBackgroundBrush"] = Brush(resources.Background);
        Resources["ShellSidebarBrush"] = Brush(resources.SidebarSurface);
        Resources["ShellSidebarBorderBrush"] = Brush(resources.SidebarBorder);
        Resources["ShellSidebarSelectionBrush"] = Brush(resources.SidebarSelectionSurface);
        Resources["ShellSurfaceBrush"] = Brush(resources.Surface);
        Resources["ShellSurfaceRaisedBrush"] = Brush(resources.RaisedSurface);
        Resources["ShellSurfaceHoverBrush"] = Brush(resources.HoverSurface);
        Resources["ShellBorderBrush"] = Brush(resources.Border);
        Resources["ShellControlSurfaceBrush"] = Brush(resources.ControlSurface);
        Resources["ShellControlBorderBrush"] = Brush(resources.ControlBorder);
        Resources["ShellControlHoverBrush"] = Brush(resources.ControlHoverSurface);
        Resources["ShellTextBrush"] = Brush(resources.Text);
        Resources["ShellMutedBrush"] = Brush(resources.MutedText);
        Resources["ShellAccentBrush"] = Brush(resources.Accent);
        Resources["ShellAccentForegroundBrush"] = Brush(resources.AccentForeground);
        Resources["ShellAccentSoftBrush"] = Brush(resources.AccentSoft);
        Resources["ShellDangerBrush"] = Brush(resources.Danger);
        Resources["ShellDangerForegroundBrush"] = Brush(resources.DangerForeground);
        Resources["ShellDangerSoftBrush"] = Brush(resources.DangerSoft);
        Resources["ShellDangerBorderBrush"] = Brush(resources.DangerBorder);
        Resources["ShellSuccessBrush"] = Brush(resources.Success);
        Resources["ShellSuccessSoftBrush"] = Brush(resources.SuccessSoft);
        Resources["ShellSuccessBorderBrush"] = Brush(resources.SuccessBorder);
        Resources["ShellWarningBrush"] = Brush(resources.Warning);
        Resources["ShellWarningSoftBrush"] = Brush(resources.WarningSoft);
        Resources["ShellWarningBorderBrush"] = Brush(resources.WarningBorder);
        Resources["ShellNoticeBorderBrush"] = Brush(resources.NoticeBorder);
        // Controls the design system has not retemplated — check boxes, radio
        // buttons, switches, sliders, calendar and text selection — take their
        // accent from Fluent's SystemAccentColor family, which otherwise tracks
        // the operating system rather than the shell's accent setting. Publishing
        // the resolved shell accent (and the shade ramp Fluent derives from it)
        // is what makes every control answer the same appearance setting.
        Resources["SystemAccentColor"] = resources.Accent;
        Resources["SystemAccentColorDark1"] = Shade(resources.Accent, Colors.Black, 0.15);
        Resources["SystemAccentColorDark2"] = Shade(resources.Accent, Colors.Black, 0.30);
        Resources["SystemAccentColorDark3"] = Shade(resources.Accent, Colors.Black, 0.45);
        Resources["SystemAccentColorLight1"] = Shade(resources.Accent, Colors.White, 0.15);
        Resources["SystemAccentColorLight2"] = Shade(resources.Accent, Colors.White, 0.30);
        Resources["SystemAccentColorLight3"] = Shade(resources.Accent, Colors.White, 0.45);
        // Text selection reads as a translucent accent wash, as the host does it,
        // rather than Fluent's opaque block.
        Resources["TextControlSelectionHighlightColor"] = Color.FromArgb(
            0x66,
            resources.Accent.R,
            resources.Accent.G,
            resources.Accent.B);
        // The focused field wears the host's accent halo — macOS's focus ring —
        // rather than only a recolored border.
        Resources["ShellFocusRingShadow"] = new BoxShadows(new BoxShadow
        {
            Blur = 0,
            Spread = 3,
            Color = Color.FromArgb(
                0x59,
                resources.Accent.R,
                resources.Accent.G,
                resources.Accent.B),
        });
        Resources["ShellUiFontFamily"] = resources.FontFamily;
        // Data surfaces (result grids, SQL editors) read in a monospace stack;
        // interface chrome stays on the UI family.
        Resources["ShellDataFontFamily"] = new FontFamily(
            "SF Mono,Menlo,Monaco,Cascadia Mono,Consolas,monospace");
        Resources["ShellBaseFontSize"] = resources.BaseFontSize;
        Resources["ShellPillFontSize"] = resources.PillFontSize;
        foreach (var baseFontSize in ScalableFontSizes)
        {
            Resources[$"ShellFontSize{baseFontSize}"] = resources.ScaleFontSize(baseFontSize);
            Resources[$"ShellLineHeight{baseFontSize}"] =
                Math.Round(
                    resources.ScaleFontSize(baseFontSize) * 1.35 * 2,
                    MidpointRounding.AwayFromZero) / 2;
        }

        Resources["ShellControlMinHeight"] = resources.ControlMinHeight;
        Resources["ShellControlCornerRadius"] = resources.ControlCornerRadius;
        Resources["ShellControlPadding"] = resources.ControlPadding;
        Resources["ShellButtonPadding"] = resources.ButtonPadding;
        Resources["ShellCardCornerRadius"] = resources.CardCornerRadius;
        Resources["ShellSidebarCornerRadius"] = resources.SidebarCornerRadius;
        Resources["ShellCardRadius"] = resources.CardCornerRadius.TopLeft;
        Resources["ShellPillCornerRadius"] = resources.PillCornerRadius;
        Resources["ShellInnerCornerRadius"] = resources.InnerCornerRadius;
        // The clearance between a rounded miniature frame and the tiles inside
        // it. It follows the radius, not a fixed step: at a tight radius a
        // couple of pixels reads fine, while a round setting needs the tiles
        // pulled in or they touch the frame's curve at every corner.
        Resources["ShellPreviewTileInset"] = new Thickness(
            Math.Max(3, resources.InnerCornerRadius.TopLeft * 0.6));

        // The spacing scale, in both forms the framework needs: a number for the
        // Spacing/ColumnSpacing/RowSpacing properties, and a Thickness for Margin
        // and Padding. Publishing only one of the two is why the markup fell back
        // to literals — half the properties could not consume the token.
        var spacing = resources.Spacing;
        foreach (var (name, value) in new (string, double)[]
                 {
                     // A published zero, so an inset can name every edge the same
                     // way whether or not that edge has a value.
                     ("None", 0),
                     ("Xs", spacing.ExtraSmall),
                     ("Sm", spacing.Small),
                     ("Md", spacing.Medium),
                     ("Lg", spacing.Large),
                     ("Xl", spacing.ExtraLarge),
                     ("Xxl", spacing.Huge),
                 })
        {
            Resources[$"ShellSpace{name}"] = value;
            Resources[$"ShellInset{name}"] = new Thickness(value);
        }

        // Named insets, for the three shapes that recur often enough that spelling
        // them out at each use site is how they drifted apart in the first place.
        Resources["ShellCardPadding"] = new Thickness(spacing.Large);
        Resources["ShellPagePadding"] = new Thickness(spacing.Large);
        Resources["ShellRowPadding"] = new Thickness(spacing.Large, spacing.Medium);
        Resources["ShellPillPadding"] = new Thickness(spacing.Small, spacing.ExtraSmall);

        // For a native child view that has to round its own layer because Avalonia
        // cannot clip it. Only the bottom corners are at the panel's edge — a
        // header covers the top two — so rounding all four would carve notches
        // into the middle of the panel rather than shaping its outline.
        Resources["ShellPanelBottomCornerRadius"] = new CornerRadius(
            0,
            0,
            resources.CardCornerRadius.BottomRight,
            resources.CardCornerRadius.BottomLeft);
        Resources["ShellAppearanceStatus"] = resources.AppearanceStatus;
        Resources["ShellAccentStatus"] = resources.AccentStatus;
        Resources["ShellPlatformProfileClass"] = resources.ProfileClass;
        Resources["ShellHighContrast"] = resources.HighContrast;
        Resources["ShellMotionEnabled"] = resources.MotionEnabled;
        Resources["ShellAdvancedMaterialsEnabled"] = resources.AdvancedMaterialsEnabled;
    }

    private void ApplyToWindow(
        Window window,
        EffectiveAppearanceResources resources)
    {
        window.RequestedThemeVariant = resources.ThemeVariant;
        foreach (var appearanceClass in AppearanceClasses)
        {
            window.Classes.Remove(appearanceClass);
        }

        window.Classes.Add(resources.ProfileClass);
        window.Classes.Add(resources.AppearanceClass);
        if (resources.HighContrast)
        {
            window.Classes.Add("high-contrast");
        }
        if (!resources.MotionEnabled)
        {
            window.Classes.Add("motion-disabled");
        }
        if (resources.AdvancedMaterialsEnabled)
        {
            window.Classes.Add("materials-enabled");
        }

    }

    private static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>
    /// One step of the accent shade ramp: the accent blended toward black or
    /// white, mirroring how Fluent derives its Dark1–3/Light1–3 variants from
    /// the system accent it is being asked to forget.
    /// </summary>
    private static Color Shade(Color accent, Color target, double amount) =>
        Color.FromRgb(
            BlendChannel(accent.R, target.R, amount),
            BlendChannel(accent.G, target.G, amount),
            BlendChannel(accent.B, target.B, amount));

    private static byte BlendChannel(byte from, byte to, double amount) =>
        (byte)Math.Round(from + ((to - from) * Math.Clamp(amount, 0, 1)));

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _ = e;
        if (sender is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit -= OnDesktopExit;
        }

        _quickTerminalController?.Dispose();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow mainWindow)
        {
            mainWindow.Closed -= OnMainWindowClosed;
        }

        // macOS can keep the native application run loop alive after every window closes.
        // The main window remains the explicit desktop lifetime owner even though Quick Terminal
        // is a reusable hidden top-level window.
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private async void OnStartupWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not MainWindow owner
            || _startupState is null)
        {
            return;
        }

        owner.Opened -= OnStartupWindowOpened;
        // With keys sealed under the startup PIN, the run begins behind the
        // lock screen; the restore preference and the recovery decision both
        // live in the database that only exists to us after that.
        await _startupState.Initialized;
        if (_startupState.RecoveryState != RecoveryDecisionState.Pending)
        {
            _ = await MainWindowViewModel.RestoreSessionOnStartupAsync(
                CancellationToken.None);
            return;
        }

        _ = await MainWindowViewModel.LoadSessionRestorePreferenceAsync(
            CancellationToken.None);
        if (_recoveryCoordinator is null)
        {
            return;
        }

        var choice = await new RecoveryDialog().ShowDialog<RecoveryChoice>(owner);
        var result = await _recoveryCoordinator.ResolveAsync(choice, CancellationToken.None);
        if (!result.IsSuccess)
        {
            await new OperationErrorDialog(
                $"Recovery could not be completed ({result.Error!.Code}).")
                .ShowDialog(owner);
            owner.Close();
            return;
        }

        _startupState.ResolveRecovery(choice, result.Value!);
        _ = await MainWindowViewModel.ApplyStartupRecoveryAsync(
            _startupState,
            CancellationToken.None);
    }
}
