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

    /// <summary>
    /// The accent of the workspace that is open, when it has one of its own. A
    /// workspace accent is a temporary override of the application's: it lasts
    /// while that workspace is open and leaves nothing behind, which is why it
    /// is held here rather than written to the stored theme.
    /// </summary>
    private RgbColor? _workspaceAccent;

    public void SetWorkspaceAccent(RgbColor? accent)
    {
        if (_workspaceAccent == accent)
        {
            return;
        }

        _workspaceAccent = accent;
        ApplyAppearance();
    }

    private EffectiveAppearanceResources ResolveAppearanceResources()
    {
        var preference = _definitionCatalog?.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)?.Value
            ?? ThemePreference.Default;
        var hostAppearance = _hostAppearance?.GetCurrent()
            ?? throw new InvalidOperationException("Platform appearance settings are unavailable.");
        var theme = preference.Resolve(hostAppearance);
        if (_workspaceAccent is { } workspaceAccent)
        {
            theme = theme with
            {
                Accent = workspaceAccent,
                AccentSource = AccentSource.Custom,
            };
        }

        return EffectiveAppearanceResourceMapper.Map(theme);
    }

    /// <summary>
    /// Publishes one appearance token, and only when it has actually changed.
    ///
    /// Every write to the application's resources invalidates each control
    /// bound to that key, anywhere in the window. There are seventy of these
    /// and almost all of them are the same before and after — a workspace
    /// accent moves the accent family and nothing else — so rewriting the set
    /// wholesale restyled the entire tree to change fifteen colours. That is
    /// most of a third of a second on every workspace switch.
    ///
    /// Brushes are compared by colour rather than by reference, because each
    /// pass builds new ones; without that every write still looks like a
    /// change and nothing is saved.
    /// </summary>
    /// <summary>
    /// A surface colour, carrying the shell's surface opacity — or solid, when
    /// the backdrop is off and there is nothing behind it to show.
    /// </summary>
    private SolidColorBrush Translucent(Color color) =>
        new(Color.FromArgb(
            WindowIsTranslucent ? ShellSurfaceAlpha : byte.MaxValue,
            color.R,
            color.G,
            color.B));

    /// <summary>
    /// The same, as a number, for the surfaces that are drawn rather than
    /// filled — a terminal paints its own background from its palette.
    /// </summary>
    private double SurfaceOpacity =>
        WindowIsTranslucent ? ShellSurfaceAlpha / (double)byte.MaxValue : 1;

    private void Publish(string key, object? value)
    {
        if (Resources.TryGetValue(key, out var existing)
            && AppearanceValuesMatch(existing, value))
        {
            return;
        }

        Resources[key] = value;
    }

    private static bool AppearanceValuesMatch(object? existing, object? value) =>
        (existing, value) switch
        {
            (ISolidColorBrush left, ISolidColorBrush right) =>
                left.Color == right.Color
                && left.Opacity.Equals(right.Opacity),
            (FontFamily left, FontFamily right) =>
                string.Equals(left.Name, right.Name, StringComparison.Ordinal),
            _ => Equals(existing, value),
        };

    /// <summary>
    /// How much of the base surface survives.
    ///
    /// The surface stays the dark it always was — this is a hint of what is
    /// behind the window, not a window you can see through. Seventy per cent
    /// let so much light in that the shell read as pale bands around dark
    /// panels; the earlier eighty-five looked like nothing only because the
    /// window was still opaque at the time and none of it was reaching the
    /// screen. This is the number to move in either direction.
    /// </summary>
    private byte ShellBackdropAlpha => (byte)Math.Clamp(
        (int)Math.Round(WindowBackdropOpacityPercent * byte.MaxValue / 100.0),
        0,
        byte.MaxValue);

    /// <summary>
    /// How solid the surfaces standing on the base are.
    ///
    /// They were fully opaque, which made the shell a pale frame around dark
    /// slabs — and the frame is what read as a bar along the top, because the
    /// chrome is the largest run of base surface not covered by a panel. The
    /// system title bar was quietened and the band stayed, which is what ruled
    /// that out and left this. Nearly solid, because text is read on these.
    /// </summary>
    /// <summary>
    /// How solid the surfaces standing on the base are.
    ///
    /// As glass they carry the base's own opacity, so the shell reads as one
    /// sheet of material rather than panels standing on it. Otherwise they sit
    /// most of the way to solid — halfway between the base and opaque — which
    /// is what the shell looked like before the choice existed, and text is
    /// read on them.
    /// </summary>
    private byte ShellSurfaceAlpha => WindowHasGlassPanels
        ? ShellBackdropAlpha
        : (byte)Math.Clamp(
            ShellBackdropAlpha + ((byte.MaxValue - ShellBackdropAlpha) / 2),
            0,
            byte.MaxValue);


    /// <summary>
    /// Whether the host has asked for less transparency. The shell answers it
    /// the same way the Quick Terminal does: no translucency and no blur.
    /// </summary>
    public bool PrefersReducedTransparency =>
        _hostAppearance?.GetCurrent().ReducedTransparency == true;

    /// <summary>
    /// How far to blur behind the shell, as stored — zero when the person has
    /// turned it off, and zero when the host asks for reduced transparency,
    /// which is the same answer arrived at two ways.
    /// </summary>
    public bool WindowIsTranslucent =>
        !PrefersReducedTransparency && StoredTheme.IsTranslucent;

    /// <summary>
    /// How solid the base surface is, as stored — fully solid when the host
    /// asks for reduced transparency, which is the same answer the blur gives
    /// to the same question.
    /// </summary>
    public int WindowBackdropOpacityPercent =>
        PrefersReducedTransparency ? 100 : StoredTheme.BackdropOpacityPercent;

    /// <summary>
    /// Whether the panels are glass. Reduced transparency says no, the same
    /// way it says no to the base surface.
    /// </summary>
    public bool WindowHasGlassPanels =>
        WindowIsTranslucent && StoredTheme.HasGlassPanels;

    private ThemePreference StoredTheme =>
        _definitionCatalog?.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)
            ?.Value ?? ThemePreference.Default;

    private void ApplyApplicationResources(EffectiveAppearanceResources resources)
    {
        Publish("ShellBackgroundBrush", Brush(resources.Background));
        // The base surface the whole shell sits on. Slightly translucent, so
        // the blurred desktop reads through the gaps between panels — but only
        // where the host is willing: reduced transparency is an accessibility
        // setting, and an operating system that says so gets a solid surface.
        Publish(
            "ShellWindowBackdropBrush",
            new SolidColorBrush(Color.FromArgb(
                WindowIsTranslucent
                    ? ShellBackdropAlpha
                    : byte.MaxValue,
                resources.Background.R,
                resources.Background.G,
                resources.Background.B)));
        Publish("ShellSidebarBrush", Translucent(resources.SidebarSurface));
        Publish("ShellSidebarBorderBrush", Brush(resources.SidebarBorder));
        Publish("ShellSidebarSelectionBrush", Brush(resources.SidebarSelectionSurface));
        Publish("ShellSurfaceBrush", Translucent(resources.Surface));
        Publish("ShellSurfaceRaisedBrush", Translucent(resources.RaisedSurface));
        Publish("ShellSurfaceHoverBrush", Brush(resources.HoverSurface));
        Publish("ShellBorderBrush", Brush(resources.Border));
        Publish("ShellControlSurfaceBrush", Brush(resources.ControlSurface));
        Publish("ShellControlBorderBrush", Brush(resources.ControlBorder));
        Publish("ShellControlHoverBrush", Brush(resources.ControlHoverSurface));
        Publish("ShellTextBrush", Brush(resources.Text));
        Publish("ShellMutedBrush", Brush(resources.MutedText));
        Publish("ShellAccentBrush", Brush(resources.Accent));
        Publish("ShellAccentForegroundBrush", Brush(resources.AccentForeground));
        Publish("ShellAccentSoftBrush", Brush(resources.AccentSoft));
        Publish("ShellDangerBrush", Brush(resources.Danger));
        Publish("ShellDangerForegroundBrush", Brush(resources.DangerForeground));
        Publish("ShellDangerSoftBrush", Brush(resources.DangerSoft));
        Publish("ShellDangerBorderBrush", Brush(resources.DangerBorder));
        Publish("ShellSuccessBrush", Brush(resources.Success));
        Publish("ShellSuccessSoftBrush", Brush(resources.SuccessSoft));
        Publish("ShellSuccessBorderBrush", Brush(resources.SuccessBorder));
        Publish("ShellWarningBrush", Brush(resources.Warning));
        Publish("ShellWarningSoftBrush", Brush(resources.WarningSoft));
        Publish("ShellWarningBorderBrush", Brush(resources.WarningBorder));
        Publish("ShellNoticeBorderBrush", Brush(resources.NoticeBorder));
        // Controls the design system has not retemplated — check boxes, radio
        // buttons, switches, sliders, calendar and text selection — take their
        // accent from Fluent's SystemAccentColor family, which otherwise tracks
        // the operating system rather than the shell's accent setting. Publishing
        // the resolved shell accent (and the shade ramp Fluent derives from it)
        // is what makes every control answer the same appearance setting.
        Publish("SystemAccentColor", resources.Accent);
        Publish("SystemAccentColorDark1", Shade(resources.Accent, Colors.Black, 0.15));
        Publish("SystemAccentColorDark2", Shade(resources.Accent, Colors.Black, 0.30));
        Publish("SystemAccentColorDark3", Shade(resources.Accent, Colors.Black, 0.45));
        Publish("SystemAccentColorLight1", Shade(resources.Accent, Colors.White, 0.15));
        Publish("SystemAccentColorLight2", Shade(resources.Accent, Colors.White, 0.30));
        Publish("SystemAccentColorLight3", Shade(resources.Accent, Colors.White, 0.45));
        // Text selection reads as a translucent accent wash, as the host does it,
        // rather than Fluent's opaque block.
        Publish("TextControlSelectionHighlightColor", Color.FromArgb(
            0x66,
            resources.Accent.R,
            resources.Accent.G,
            resources.Accent.B));
        // The focused field wears the host's accent halo — macOS's focus ring —
        // rather than only a recolored border.
        Publish("ShellFocusRingShadow", new BoxShadows(new BoxShadow
        {
            Blur = 0,
            Spread = 3,
            Color = Color.FromArgb(
                0x59,
                resources.Accent.R,
                resources.Accent.G,
                resources.Accent.B),
        }));
        Publish("ShellUiFontFamily", resources.FontFamily);
        // Data surfaces (result grids, SQL editors) read in a monospace stack;
        // interface chrome stays on the UI family.
        Publish("ShellDataFontFamily", new FontFamily(
            "SF Mono,Menlo,Monaco,Cascadia Mono,Consolas,monospace"));
        Publish("ShellBaseFontSize", resources.BaseFontSize);
        Publish("ShellPillFontSize", resources.PillFontSize);
        foreach (var baseFontSize in ScalableFontSizes)
        {
            Publish($"ShellFontSize{baseFontSize}", resources.ScaleFontSize(baseFontSize));
            Publish(
                $"ShellLineHeight{baseFontSize}",
                Math.Round(
                    resources.ScaleFontSize(baseFontSize) * 1.35 * 2,
                    MidpointRounding.AwayFromZero) / 2);
        }

        Publish("ShellControlMinHeight", resources.ControlMinHeight);
        Publish("ShellSurfaceOpacity", SurfaceOpacity);

        // The three sizes a mark is drawn at — in a row, beside a name, and as
        // the subject of the page. Derived from the control height rather than
        // fixed, so a compact density shrinks the tiles with everything else
        // instead of leaving them looming over the rows they belong to.
        Publish("ShellTileSizeSm", Math.Round(resources.ControlMinHeight * 0.85));
        Publish("ShellTileSizeMd", Math.Round(resources.ControlMinHeight * 1.05));
        Publish("ShellTileSizeLg", Math.Round(resources.ControlMinHeight * 1.75));

        // The rail tile's outline, and how wide it opens when it offers to close
        // the workspace. Twice its own width, so the action it reveals is the
        // same size as the tile it belongs to rather than a sliver beside it.
        Publish("ShellWorkspaceRailRing", new Thickness(
            Math.Max(1, Math.Round(resources.ControlMinHeight * 0.05))));
        Publish(
            "ShellWorkspaceRailTileExpandedWidth",
            Math.Round(resources.ControlMinHeight * 1.05) * 2);

        // The attention dot, and the ring that keeps it legible on top of a
        // saturated tile. Derived like the tiles so a compact density does not
        // leave a mark sized for a roomier one.
        Publish("ShellSignalDotSize", Math.Round(resources.ControlMinHeight * 0.28));
        Publish("ShellSignalDotRing", new Thickness(
            Math.Max(1, Math.Round(resources.ControlMinHeight * 0.06))));
        Publish("ShellControlCornerRadius", resources.ControlCornerRadius);
        Publish("ShellControlPadding", resources.ControlPadding);
        Publish("ShellButtonPadding", resources.ButtonPadding);
        Publish("ShellCardCornerRadius", resources.CardCornerRadius);
        Publish("ShellSidebarCornerRadius", resources.SidebarCornerRadius);
        Publish("ShellCardRadius", resources.CardCornerRadius.TopLeft);
        Publish("ShellPillCornerRadius", resources.PillCornerRadius);
        Publish("ShellInnerCornerRadius", resources.InnerCornerRadius);
        // The clearance between a rounded miniature frame and the tiles inside
        // it. It follows the radius, not a fixed step: at a tight radius a
        // couple of pixels reads fine, while a round setting needs the tiles
        // pulled in or they touch the frame's curve at every corner.
        Publish("ShellPreviewTileInset", new Thickness(
            Math.Max(3, resources.InnerCornerRadius.TopLeft * 0.6)));

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
            Publish($"ShellSpace{name}", value);
            Publish($"ShellInset{name}", new Thickness(value));
        }

        // Named insets, for the three shapes that recur often enough that spelling
        // them out at each use site is how they drifted apart in the first place.
        Publish("ShellCardPadding", new Thickness(spacing.Large));
        Publish("ShellPagePadding", new Thickness(spacing.Large));
        Publish("ShellRowPadding", new Thickness(spacing.Large, spacing.Medium));
        Publish("ShellPillPadding", new Thickness(spacing.Small, spacing.ExtraSmall));

        // For a native child view that has to round its own layer because Avalonia
        // cannot clip it. Only the bottom corners are at the panel's edge — a
        // header covers the top two — so rounding all four would carve notches
        // into the middle of the panel rather than shaping its outline.
        Publish("ShellPanelBottomCornerRadius", new CornerRadius(
            0,
            0,
            resources.CardCornerRadius.BottomRight,
            resources.CardCornerRadius.BottomLeft));
        Publish("ShellAppearanceStatus", resources.AppearanceStatus);
        Publish("ShellAccentStatus", resources.AccentStatus);
        Publish("ShellPlatformProfileClass", resources.ProfileClass);
        Publish("ShellHighContrast", resources.HighContrast);
        Publish("ShellMotionEnabled", resources.MotionEnabled);
        Publish("ShellAdvancedMaterialsEnabled", resources.AdvancedMaterialsEnabled);
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
        // The backdrop is a stored setting, so a change to it has to reach the
        // window that is wearing it rather than only the next one opened.
        (window as MainWindow)?.RefreshWindowBackdrop();
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
        // The history load the window construction queued hit that same
        // closed database; asked again now, it answers.
        _ = MainWindowViewModel.RetryRecentSessionHistoryAsync(CancellationToken.None);
        if (_startupState.RecoveryState != RecoveryDecisionState.Pending)
        {
            _ = await MainWindowViewModel.RestoreSessionOnStartupAsync(
                CancellationToken.None);
            // Whatever the restore did or did not find, the window does not
            // come up empty: Main is always there to come up in.
            _ = await MainWindowViewModel.OpenDefaultWorkspaceIfIdleAsync(
                CancellationToken.None);
            return;
        }

        _ = await MainWindowViewModel.LoadSessionRestorePreferenceAsync(
            CancellationToken.None);
        if (_recoveryCoordinator is null)
        {
            _ = await MainWindowViewModel.OpenDefaultWorkspaceIfIdleAsync(
                CancellationToken.None);
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
        _ = await MainWindowViewModel.OpenDefaultWorkspaceIfIdleAsync(
            CancellationToken.None);
    }
}
