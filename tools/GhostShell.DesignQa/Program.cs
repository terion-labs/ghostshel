using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GhostShell.App.ViewModels;
using GhostShell.App.Views;
using GhostShell.App.Controls;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.DesignQa;

/// <summary>
/// Presentation-only capture harness. It renders the product's real
/// <see cref="MainWindow"/>, styles, and view models against a deterministic
/// in-memory fixture and writes one PNG per route. Rendering happens in-process
/// through <see cref="RenderTargetBitmap"/>, so it needs no screen-recording
/// permission and never captures unrelated windows.
/// </summary>
internal static class Program
{
    public static string OutputDirectory { get; private set; } = string.Empty;

    public static string[] RequestedRoutes { get; private set; } = [];

    public static bool IsTerminalFontVerification { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--verify-terminal-font"])
        {
            IsTerminalFontVerification = true;
            BuildAvaloniaApp().SetupWithoutStarting();
            VerifyTerminalFont();
            return;
        }

        OutputDirectory = Path.GetFullPath(
            args.FirstOrDefault() ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "design-qa", "current"));
        RequestedRoutes = args.Skip(1).ToArray();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<QaApplication>()
            // Headless with real Skia drawing: every capture already renders
            // offscreen through RenderTargetBitmap, so the on-screen windows
            // only ever existed to pump layout — and stole focus doing it.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            })
            .WithInterFont()
            .ConfigureFonts(fontManager =>
                fontManager.AddFontCollection(new GhostShellTerminalFontCollection()))
            .LogToTrace();

    private static void VerifyTerminalFont()
    {
        var faces = new[]
        {
            (File: "JetBrainsMono-Regular.ttf", Style: FontStyle.Normal, Weight: FontWeight.Normal),
            (File: "JetBrainsMono-Bold.ttf", Style: FontStyle.Normal, Weight: FontWeight.Bold),
            (File: "JetBrainsMono-Italic.ttf", Style: FontStyle.Italic, Weight: FontWeight.Normal),
            (File: "JetBrainsMono-BoldItalic.ttf", Style: FontStyle.Italic, Weight: FontWeight.Bold),
        };

        foreach (var face in faces)
        {
            var assetUri = new Uri(
                $"avares://GhostShell.App/Assets/Fonts/JetBrainsMono/{face.File}",
                UriKind.Absolute);
            using var asset = AssetLoader.Open(assetUri);
            if (asset.Length == 0)
            {
                throw new InvalidOperationException($"Embedded terminal font is empty: {face.File}.");
            }

            var typeface = new Typeface(
                GhostShellTerminalFontCollection.Family,
                face.Style,
                face.Weight);
            if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface)
                || glyphTypeface.FamilyName != GhostShellTerminalFontCollection.FamilyName
                || glyphTypeface.Style != face.Style
                || glyphTypeface.Weight != face.Weight
                || glyphTypeface.Stretch != FontStretch.Normal
                || glyphTypeface.FontSimulations != FontSimulations.None
                || !glyphTypeface.Metrics.IsFixedPitch)
            {
                throw new InvalidOperationException(
                    $"Embedded terminal font did not resolve to its exact fixed-pitch face: {face.File}.");
            }
        }

        Console.WriteLine(
            $"Verified embedded {GhostShellTerminalFontCollection.FamilyName}: "
            + "regular, bold, italic, and bold italic are fixed-pitch native faces.");
    }
}

internal sealed record RouteCapture(
    string Name,
    Action<MainWindowViewModel> Apply,
    string? FocusFirst = null,
    int Height = 900,
    ThemePreference? Theme = null,
    string? ClickFirst = null,
    Action<MainWindow>? PrepareCapture = null);

internal sealed class QaApplication : Avalonia.Application
{
    private static readonly QaAiProfileRuntime AgentProfiles = new();

    private static readonly QaOfflineAgentRuntime AgentRuntime = new();

    private static readonly EmptyFileClients Files = new();

    private static readonly RouteCapture[] Routes =
    [
        new("launcher-home", vm => vm.ShowLauncher()),
        new("launcher-connections", vm => vm.ShowLauncherConnections()),
        new(
            "launcher-connections-hover",
            vm => vm.ShowLauncherConnections(),
            PrepareCapture: ShowFirstLaunchCardHover),
        new("launcher-screens", vm => vm.ShowLauncherScreens()),
        new("launcher-history", vm => vm.ShowLauncherHistory()),
        new("settings-appearance", vm => vm.ShowSettings(SettingsPage.Appearance)),
        new("settings-workspaces", vm => vm.ShowSettings(SettingsPage.Workspaces)),
        new("settings-terminal", vm => vm.ShowSettings(SettingsPage.Terminal)),
        new("settings-quick-terminal", vm => vm.ShowSettings(SettingsPage.QuickTerminal)),
        new("settings-keybindings", vm => vm.ShowSettings(SettingsPage.Keybindings)),
        new("settings-files", vm => vm.ShowSettings(SettingsPage.Files)),
        new("settings-agent", vm => vm.ShowSettings(SettingsPage.Agent)),
        new("settings-mcp", vm => vm.ShowSettings(SettingsPage.Mcp)),
        new("settings-secrets", vm => vm.ShowSettings(SettingsPage.Secrets)),
        new("settings-diagnostics", vm => vm.ShowSettings(SettingsPage.Diagnostics)),
        new("settings-about", vm => vm.ShowSettings(SettingsPage.About)),
        new("overlay-command-palette", vm =>
        {
            vm.ShowWorkspace();
            vm.ShowOverlay(ShellOverlay.CommandPalette);
        }),
        new("overlay-new-item", vm =>
        {
            vm.ShowLauncher();
            vm.ShowOverlay(ShellOverlay.NewItem);
        }),
        new("overlay-new-panel", vm =>
        {
            vm.ShowWorkspace();
            vm.ShowOverlay(ShellOverlay.NewPanel);
        }),
        // Opened on a real layout: an empty designer hides every defect the
        // populated one would show.
        new("overlay-layout-designer", vm =>
        {
            vm.ShowWorkspace();
            vm.BeginEditLayout(new LayoutId("grid-four"));
        }),
        new("workspace", vm => vm.ShowWorkspace()),
        new(
            "workspace-drag-ghost",
            vm => vm.ShowWorkspace(),
            PrepareCapture: ShowSampleDragGhost),
        new(
            "workspace-transfers",
            vm =>
            {
                vm.ShowWorkspace();
                Files.PublishSampleTransfer();
            },
            ClickFirst: "Open transfer manager"),
        // The agent panel's conversation layout is otherwise unreviewable: the
        // harness has no provider and reaches no endpoint, so every other route
        // can only render the panel's empty state.
        new("workspace-agent", vm =>
        {
            vm.ShowWorkspace();
            AgentProfiles.PublishSampleProfile();
            AgentRuntime.PublishSampleConversation();
        }),
        // The one governance decision the panel ever asks. It was the panel's
        // least reviewed surface for exactly that reason.
        new("workspace-agent-capability", vm =>
        {
            vm.ShowWorkspace();
            AgentProfiles.PublishSampleProfile();
            AgentRuntime.PublishSampleCapabilityRequest();
        }),
        // The database viewer with a connected stub: table list, query editor,
        // and a populated result grid. Last workspace route, because the added
        // panel stays in the shared fixture.
        new("workspace-database", vm =>
        {
            vm.ShowWorkspace();
            AddSampleDatabasePanel(vm);
        }),
        new("settings-workspace-editor", vm =>
        {
            vm.ShowSettings(SettingsPage.Workspaces);
            vm.BeginEditWorkspace(new WorkspaceId("operations"));
        }, Height: 1200),
        // Keyboard focus has its own visuals; capturing it keeps the focus ring
        // reviewable instead of only reachable by hand.
        new("settings-appearance-focused", vm => vm.ShowSettings(SettingsPage.Appearance), FocusFirst: "SettingsBackButton"),
        // The whole settings-apply-immediately loop, end to end: the click
        // commits the theme, the catalog change re-publishes the resources, and
        // the capture must visibly densify against plain settings-appearance.
        new(
            "settings-appearance-density-compact",
            vm => vm.ShowSettings(SettingsPage.Appearance),
            ClickFirst: "Compact padding density"),
        // Long settings pages are also captured whole, so a section below the
        // fold is reviewable without scrolling by hand.
        new("settings-appearance-full", vm => vm.ShowSettings(SettingsPage.Appearance), Height: 2100),
        new("settings-terminal-full", vm => vm.ShowSettings(SettingsPage.Terminal), Height: 1500),
        // The corner and density settings are only worth having if they visibly
        // reshape the interface. Capturing both extremes makes a regression that
        // silently disconnects them show up as two identical images.
        new(
            "appearance-corners-tight",
            vm => vm.ShowLauncher(),
            Theme: AppearanceExtreme(cornerRadius: 0, InterfaceDensity.Compact)),
        new(
            "appearance-corners-round",
            vm => vm.ShowLauncher(),
            Theme: AppearanceExtreme(cornerRadius: 20, InterfaceDensity.Comfortable)),
    ];

    /// <summary>
    /// A theme that differs from the default only in corner radius and density,
    /// so a comparison between the two captures isolates those two settings.
    /// </summary>
    private static ThemePreference AppearanceExtreme(
        double cornerRadius,
        InterfaceDensity density) =>
        new(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            AppearanceMode.Dark,
            PlatformProfile.Automatic,
            AccentPreference.FollowHost,
            cornerRadiusOverride: cornerRadius,
            density: density);

    private static void ShowSampleDragGhost(MainWindow window)
    {
        var payloadType = typeof(MainWindow).Assembly.GetType(
            "GhostShell.App.Views.Components.DragGhostPayload",
            throwOnError: true)!;
        var constructor = AssertSingle(payloadType.GetConstructors());
        var symbolType = constructor.GetParameters()[0].ParameterType;
        var payload = constructor.Invoke(
        [
            Enum.Parse(symbolType, "Document"),
            "Archive.zip",
            "Copy from dev.terion.pro",
        ]);
        var show = typeof(MainWindow).GetMethod(
            "ShowDragGhost",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The main window no longer exposes its internal drag-ghost presentation seam.");
        _ = show.Invoke(window, [payload, new Point(760, 420)]);
    }

    private static void ShowFirstLaunchCardHover(MainWindow window)
    {
        var cardSurface = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("CardSurface"))
            ?? throw new InvalidOperationException(
                "The launcher no longer exposes a CardSurface button for hover QA.");
        var pseudoClasses = typeof(StyledElement).GetProperty(
                "PseudoClasses",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(cardSurface) as IPseudoClasses
            ?? throw new InvalidOperationException(
                "Avalonia no longer exposes the protected pseudo-class collection used by QA.");

        pseudoClasses.Add(":pointerover");
    }

    private static T AssertSingle<T>(IReadOnlyList<T> values) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException(
                $"Expected one value but found {values.Count}.");

    /// <summary>
    /// Replicas of the workspaces-rail buttons, one per representative symbol,
    /// built exactly like WorkspaceView's rail tiles so glyph placement in the
    /// capture is the product's.
    /// </summary>
    private static Window CreateRailTileProbe()
    {
        var symbols = new[]
        {
            FluentIcons.Common.Symbol.Window,
            FluentIcons.Common.Symbol.WindowConsole,
            FluentIcons.Common.Symbol.Code,
            FluentIcons.Common.Symbol.Rocket,
            FluentIcons.Common.Symbol.Database,
        };
        var stack = new StackPanel
        {
            Margin = new Thickness(8),
            Spacing = 8,
        };
        foreach (var symbol in symbols)
        {
            stack.Children.Add(new Button
            {
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#C77828")),
                Content = new FluentIcons.Avalonia.SymbolIcon
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Symbol = symbol,
                    FontSize = 16,
                },
            });
        }

        stack.Children.Add(new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Content = new FluentIcons.Avalonia.SymbolIcon
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Symbol = FluentIcons.Common.Symbol.Add,
                FontSize = 14,
            },
        });
        return new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Content = stack,
        };
    }

    /// <summary>
    /// The unified editor with all three families available, matching the shell's
    /// launcher composition. An existing terminal connection pins the editor to
    /// the terminal family, like editing from the launcher does.
    /// </summary>
    private static UnifiedConnectionEditorViewModel CreateQaConnectionEditor(
        bool existingTerminal = false,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        var terminal = existingTerminal
            ? new ConnectionEditorViewModel(
                new UnusedConnectionRuntime(),
                QaData.Connections[0].Value,
                QaData.Connections[0].Revision)
            : new ConnectionEditorViewModel(new UnusedConnectionRuntime());
        var connections = QaData.Connections.Select(item => item.Value).ToArray();
        return new UnifiedConnectionEditorViewModel(
            terminal,
            new FileProviderProfileEditorViewModel(
                new QaFileProviderRuntime(),
                connections,
                []),
            new DatabaseConnectionEditorViewModel(
                new QaDatabasePanelClient(),
                connections),
            lockedFamily: existingTerminal ? SavedConnectionFamily.Terminal : null,
            initialFamily: initialFamily);
    }

    /// <summary>
    /// Modal editors and confirmations are their own windows, so they are
    /// captured directly rather than through a shell route.
    /// </summary>
    private static readonly (string Name, Func<Window> Create, ThemePreference? Theme)[] Dialogs =
    [
        // The workspaces-rail tiles at Retina density: every icon the rail can
        // draw, so glyph centering is measurable at the scale users run at.
        ("rail-tiles-2x", CreateRailTileProbe, null),
        // The same chooser the placeholder panel embeds, at a split-panel width,
        // so the adaptive tile grid is reviewable at the size that used to crush
        // its labels to one letter.
        ("chooser-narrow", () => new Window
        {
            Width = 480,
            Height = 820,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Classes = { "FloatingSidebar" },
                Padding = new Thickness(16),
                Child = new GhostShell.App.Views.Components.NewItemChooserView
                {
                    DataContext = CreateViewModel(),
                },
            },
        }, null),
        ("dialog-connection-editor", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor()), null),
        ("dialog-connection-editor-existing", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor(existingTerminal: true)), null),
        ("dialog-connection-editor-files", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor(
                initialFamily: SavedConnectionFamily.Files)), null),
        ("dialog-connection-editor-database", () => new ConnectionEditorDialog(
            CreateQaConnectionEditor(
                initialFamily: SavedConnectionFamily.Database)), null),
        ("dialog-ai-provider-editor", () => new AiProviderProfileEditorDialog(
            new AiProviderProfileEditorViewModel(new QaAiProfileRuntime(), [])), null),
        ("dialog-database-connection", () => new DatabaseConnectionDetailsDialog(
            "PostgreSQL",
            isFileBased: false,
            new GhostShell.Application.DatabaseConnectionDetails(
                "db.internal",
                5432,
                "coreapi",
                "ops",
                "s3cret",
                null,
                "SSL Mode=Require")), null),
        ("dialog-mcp-server-editor", () => new McpServerProfileEditorDialog(
            new McpServerProfileEditorViewModel()), null),
        ("dialog-saved-screen-editor", () => new SavedScreenEditorDialog(
            new SavedScreenEditorViewModel(
                QaData.Screens[0].Value,
                QaData.Screens[0].Revision,
                QaData.Connections.Select(item => item.Value).ToArray(),
                [],
                QaData.Layouts.Select(item => item.Value).ToArray()),
            // The harness never persists; capture is presentation only.
            static (_, _) => throw new NotSupportedException(
                "The design QA harness does not save definitions.")), null),
        // The design system itself, so a changed radius, gap, or tone shows up as a
        // diff in one image rather than as drift discovered later in a screenshot.
        ("design-system", static () => new DesignSystemGalleryWindow(), null),
        ("dialog-definition-delete", () => new DefinitionDeleteDialog(
            "connection",
            QaData.Connections[0].Value.Name), null),
        // The same gallery at the two density extremes. The spacing scale, the
        // radii, and the control metrics all derive from the settings, so if any
        // of them stops doing so these two become the same image — which is the
        // only way to notice that a token quietly went back to being a literal.
        ("design-system-compact",
            static () => new DesignSystemGalleryWindow(),
            AppearanceExtreme(cornerRadius: 0, InterfaceDensity.Compact)),
        ("design-system-comfortable",
            static () => new DesignSystemGalleryWindow(),
            AppearanceExtreme(cornerRadius: 20, InterfaceDensity.Comfortable)),
        // The light appearance, which otherwise only exists on user machines.
        ("design-system-light",
            static () => new DesignSystemGalleryWindow(),
            new ThemePreference(
                ThemePreference.Default.Id,
                ThemePreference.Default.Name,
                AppearanceMode.Light,
                PlatformProfile.Automatic,
                AccentPreference.FollowHost)),
    ];

    public override void Initialize()
    {
        // Load the product's compiled resources and styles rather than a
        // harness copy that could drift from the shipped application.
        var productApplication = new GhostShell.App.App();
        productApplication.Initialize();
        _productApplication = productApplication;

        // The shipped app publishes its ShellFontSize*/metric resources from the
        // appearance pipeline at composition time. The harness has no composition
        // root, so it runs that same private mapping here; without it every
        // DynamicResource size silently falls back and the capture misreports
        // the real typography.
        ApplyProductAppearanceResources(productApplication, ThemePreference.Default);

        ((IResourceProvider)productApplication.Resources).RemoveOwner(productApplication);
        Resources = productApplication.Resources;
        while (productApplication.Styles.Count > 0)
        {
            var style = productApplication.Styles[0];
            productApplication.Styles.RemoveAt(0);
            Styles.Add(style);
        }

        RequestedThemeVariant = ThemeVariant.Dark;
    }

    private static GhostShell.App.App? _productApplication;

    /// <summary>
    /// Re-publishes the application resources for <paramref name="theme"/>. The
    /// resource dictionary is shared with this harness application, so the live
    /// window picks the new metrics up exactly as it does in the product.
    /// </summary>
    private static void ApplyTheme(ThemePreference theme)
    {
        if (_productApplication is { } productApplication)
        {
            var mapped = ApplyProductAppearanceResources(productApplication, theme);
            // The theme dictionaries key off the requested variant, exactly as
            // the product's ApplyAppearance switches it; without this a light
            // theme capture would mix light published tokens with dark
            // dictionary fallbacks.
            if (Current is { } current
                && mapped?.GetType().GetProperty("ThemeVariant")?.GetValue(mapped)
                    is ThemeVariant variant)
            {
                current.RequestedThemeVariant = variant;
            }
        }
    }

    private static object? ApplyProductAppearanceResources(
        GhostShell.App.App productApplication,
        ThemePreference theme)
    {
        // The reference frames were drawn with #FF8400 as the example host accent.
        // Supplying it here keeps captures directly comparable; the product's own
        // bronze fallback still applies whenever a host reports no accent.
        var host = new HostAppearance(
            HostOperatingSystem.MacOS,
            HostColorScheme.Dark,
            new RgbColor(0xFF, 0x84, 0x00),
            supportsAdvancedMaterials: true);
        var effectiveTheme = theme.Resolve(host);

        var appAssembly = typeof(GhostShell.App.App).Assembly;
        var mapper = appAssembly.GetType(
                "GhostShell.App.EffectiveAppearanceResourceMapper",
                throwOnError: true)!;
        var mapped = mapper
            .GetMethod("Map", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [effectiveTheme]);

        typeof(GhostShell.App.App)
            .GetMethod(
                "ApplyApplicationResources",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(productApplication, [mapped]);
        return mapped;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (Program.IsTerminalFontVerification)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            throw new InvalidOperationException("A desktop lifetime is required.");
        }

        var viewModel = CreateViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
            Title = "GhostSHELL · design QA",
            Width = 1440,
            Height = 900,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(40, 40),
        };

        desktop.MainWindow = window;
        window.Opened += async (_, _) => await CaptureAllAsync(desktop, window, viewModel);
        window.Show();

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var catalog = new QaDefinitionCatalog(QaData.Snapshot);
        // The product republishes the appearance whenever the catalog changes;
        // mirroring that here makes "settings apply immediately" capturable.
        catalog.Changed += (_, _) =>
        {
            if (catalog.SavedTheme is { } savedTheme)
            {
                ApplyTheme(savedTheme);
            }
        };
        var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, UnusedProxy>(),
            catalog,
            new UnusedConnectionRuntime(),
            new MemoryOnlySecretVault(),
            Files,
            Files,
            new TerminalStartupCommandDispatcher(new MemoryOnlyAuditStore(), TimeProvider.System),
            uiThreadDispatcher: new ImmediateUiDispatcher(),
            recentSessionHistory: new GhostShell.App.RecentSessionHistory(
                new QaRecentSessionStore(),
                new QaTimeProvider()),
            fileProviderRuntime: new QaFileProviderRuntime(),
            timeProvider: new QaTimeProvider(),
            aiProviderRuntime: AgentProfiles,
            agentChatRuntime: AgentRuntime,
            databasePanelClient: new QaDatabasePanelClient());

        // Real connection pills, so the row that carries them is reviewable
        // rather than rendering empty.
        var workspace = new RuntimeWorkspaceViewModel(
            new WorkspaceInstanceId("qa-workspace"),
            "Operations",
            "Bronze",
            viewModel.Connections.Take(2).ToArray());
        // Real tabs so the tab strip is reviewable at every placement.
        foreach (var (id, title, source) in new[]
                 {
                     ("qa-tab-api", "production-api", "production-api"),
                     ("qa-tab-web", "staging-web", "staging-web"),
                     ("qa-tab-db", "postgres-primary", "postgres-primary"),
                 })
        {
            workspace.Tabs.Add(new RuntimeTabViewModel(new TabInstanceId(id), title, source));
        }

        // Real panels, so the panel card and its header are actually rendered. The
        // harness cannot run a PTY, but the chrome around one is Avalonia's and is
        // exactly where the rounded corner is either clipped or not.
        var panels = new[]
        {
            new UnavailableRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-terminal"),
                PanelKind.Terminal,
                "production-api",
                "LOCAL",
                "This harness renders panel chrome without a live session."),
            new UnavailableRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-browser"),
                PanelKind.Browser,
                "Browser",
                "BROWSER",
                "This harness renders panel chrome without a live session."),
        };
        foreach (var panel in panels)
        {
            workspace.Tabs[0].AddPanel(panel);
        }

        _ = workspace.Tabs[0].ActivatePanel(panels[0].Id);
        workspace.Tabs[0].NotifyPanelLayoutChanged();
        workspace.Tabs[0].IsActive = true;
        // The active tab is what the canvas presents, and its setter is internal.
        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [workspace.Tabs[0]]);

        typeof(MainWindowViewModel)
            .GetProperty(nameof(MainWindowViewModel.RuntimeWorkspace))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(viewModel, [workspace]);

        return viewModel;
    }

    private static void AddSampleDatabasePanel(MainWindowViewModel viewModel)
    {
        if (viewModel.RuntimeWorkspace is not { } workspace)
        {
            return;
        }

        var tab = workspace.Tabs.FirstOrDefault(candidate =>
            candidate.Panels.Any(panel => panel is DatabaseRuntimePanelViewModel));
        if (tab is null)
        {
            tab = new RuntimeTabViewModel(
                new TabInstanceId("qa-tab-database"),
                "deployments-db",
                "Local");
            var panel = new DatabaseRuntimePanelViewModel(
                new PanelInstanceId("qa-panel-database"),
                "Database",
                new QaDatabasePanelClient(),
                driverId: "sqlite",
                connectionString: "Data Source=/srv/app/production.db");
            tab.AddPanel(panel);
            _ = tab.ActivatePanel(panel.Id);
            // The stub completes synchronously, so the capture shows real rows,
            // and a selected row exercises the field inspector.
            _ = panel.PreviewTableAsync(panel.Tables[0]);
            panel.SelectRow(panel.ResultRows[2]);
            tab.NotifyPanelLayoutChanged();
            workspace.Tabs.Add(tab);
        }

        foreach (var candidate in workspace.Tabs)
        {
            candidate.IsActive = ReferenceEquals(candidate, tab);
        }

        typeof(RuntimeWorkspaceViewModel)
            .GetProperty(nameof(RuntimeWorkspaceViewModel.ActiveTab))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(workspace, [tab]);
    }

    /// <summary>
    /// Shows a modal editor off-screen, lets it settle, then renders it at its
    /// own arranged size so the capture reflects the dialog's real geometry.
    /// </summary>
    private static async Task CaptureDialogAsync(string name, Window dialog)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(-4000, -4000);
        dialog.ShowInTaskbar = false;
        dialog.Show();
        await Task.Delay(260);
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        await Task.Delay(140);

        // A "-2x" suffix renders at Retina density, so glyph-placement issues
        // that only appear under fractional-scale pixel snapping are capturable.
        var scale = name.EndsWith("-2x", StringComparison.Ordinal) ? 2 : 1;
        var width = (int)Math.Ceiling(Math.Max(dialog.Bounds.Width, 1)) * scale;
        var height = (int)Math.Ceiling(Math.Max(dialog.Bounds.Height, 1)) * scale;
        var path = Path.Combine(Program.OutputDirectory, $"{name}.png");
        using (var bitmap = new RenderTargetBitmap(
                   new PixelSize(width, height),
                   new Vector(96 * scale, 96 * scale)))
        {
            bitmap.Render(dialog);
            bitmap.Save(path);
        }

        dialog.Close();
        Console.WriteLine($"CAPTURE {name} -> {path} ({width}x{height})");
    }

    private static async Task CaptureAllAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel viewModel)
    {
        var exitCode = 0;
        try
        {
            Directory.CreateDirectory(Program.OutputDirectory);
            await Task.Delay(800);

            var requested = Program.RequestedRoutes;
            var selected = requested.Length == 0
                ? Routes
                : Routes.Where(route => requested.Contains(route.Name)).ToArray();
            var selectedDialogs = requested.Length == 0
                ? Dialogs
                : Dialogs.Where(dialog => requested.Contains(dialog.Name)).ToArray();

            if (selected.Length == 0 && selectedDialogs.Length == 0)
            {
                var known = Routes.Select(r => r.Name).Concat(Dialogs.Select(d => d.Name));
                throw new InvalidOperationException(
                    $"No route matched. Known routes: {string.Join(", ", known)}");
            }

            foreach (var route in selected)
            {
                ApplyTheme(route.Theme ?? ThemePreference.Default);
                // The sample agent conversation belongs to the one route that asks
                // for it. Resetting first keeps that route from leaking a connected
                // agent into whatever is captured after it, whatever the order.
                AgentProfiles.Reset();
                AgentRuntime.Reset();
                Files.Reset();
                // The sample drag ghost belongs to the one route that shows it;
                // without this it floats over every capture that follows.
                typeof(MainWindow)
                    .GetMethod(
                        "HideDragGhost",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(window, []);
                // Likewise flyouts a route clicked open — the transfer manager
                // otherwise floats over every capture after its own.
                foreach (var popup in window.GetVisualDescendants()
                             .OfType<Avalonia.Controls.Primitives.Popup>()
                             .Where(popup => popup.IsOpen))
                {
                    popup.Close();
                }

                route.Apply(viewModel);
                await Task.Delay(220);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Task.Delay(120);

                if (route.FocusFirst is { } focusTarget)
                {
                    var control = window.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(candidate => candidate.Name == focusTarget)
                        ?? throw new InvalidOperationException(
                            $"The route wanted to focus '{focusTarget}', which is not in the tree.");
                    control.Focus(NavigationMethod.Tab);
                    await Task.Delay(140);
                    Dispatcher.UIThread.RunJobs();
                }

                if (route.ClickFirst is { } clickTarget)
                {
                    var button = window.GetVisualDescendants()
                        .OfType<Button>()
                        .FirstOrDefault(candidate =>
                            string.Equals(
                                AutomationProperties.GetName(candidate),
                                clickTarget,
                                StringComparison.Ordinal))
                        ?? throw new InvalidOperationException(
                            $"The route wanted to click '{clickTarget}', which is not in the tree.");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    // Long enough for debounced effects of the click — the
                    // appearance page coalesces edits for 220 ms before the
                    // theme commit re-publishes the resources.
                    await Task.Delay(500);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }

                if (window.Height != route.Height)
                {
                    window.Height = route.Height;
                    await Task.Delay(200);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    await Task.Delay(120);
                }

                if (route.PrepareCapture is { } prepareCapture)
                {
                    prepareCapture(window);
                    await Task.Delay(140);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }

                var path = Path.Combine(Program.OutputDirectory, $"{route.Name}.png");
                using (var bitmap = new RenderTargetBitmap(new PixelSize(1440, route.Height), new Vector(96, 96)))
                {
                    bitmap.Render(window);
                    bitmap.Save(path);
                }

                Console.WriteLine($"CAPTURE {route.Name} -> {path}");
            }

            foreach (var dialog in selectedDialogs)
            {
                ApplyTheme(dialog.Theme ?? ThemePreference.Default);
                await CaptureDialogAsync(dialog.Name, dialog.Create());
            }

            ApplyTheme(ThemePreference.Default);
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Console.Error.WriteLine($"FAILED {ex}");
        }
        finally
        {
            viewModel.Dispose();
            desktop.Shutdown(exitCode);
        }
    }
}
