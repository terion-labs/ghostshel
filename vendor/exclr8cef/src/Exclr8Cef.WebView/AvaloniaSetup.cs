using Avalonia;
using Avalonia.Threading;

namespace Exclr8Cef.WebView;

/// <summary>
/// One-stop initialization helpers that absorb the Avalonia-specific
/// boilerplate every consumer would otherwise have to reimplement:
///
/// <list type="bullet">
///   <item>Locating the macOS Helper.app subprocess bundle</item>
///   <item>Wiring CEF's external message pump to a <see cref="DispatcherTimer"/></item>
///   <item>Calling the right <c>Cef.Initialize*</c> flavour for OSR + Alloy</item>
///   <item>Honouring the Avalonia-first init order on macOS (libAvaloniaNative
///         must register its obj-c classes before the CEF framework loads)</item>
/// </list>
///
/// Typical use in <c>Program.cs</c>:
///
/// <code>
/// public static int Main(string[] args)
/// {
///     int subproc = Exclr8Cef.Cef.ExecuteProcess(args);
///     if (subproc >= 0) return subproc;
///
///     var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
///     BuildAvaloniaApp().UseExclr8Cef().SetupWithLifetime(lifetime);
///     AvaloniaSetup.InitializeForOsr();
///     try { return lifetime.Start(args); }
///     finally { Exclr8Cef.Cef.Shutdown(); }
/// }
///
/// public static AppBuilder BuildAvaloniaApp() =&gt;
///     AppBuilder.Configure&lt;App&gt;().UsePlatformDetect();
/// </code>
///
/// That's the whole CEF setup. The control surface (<c>&lt;cef:WebView/&gt;</c>,
/// <c>&lt;cef:NativeWebView/&gt;</c>) Just Works under this init.
/// </summary>
public static class AvaloniaSetup
{
    /// <summary>
    /// Initialize CEF for OSR-rendered Avalonia hosts. Auto-resolves the
    /// macOS Helper.app under <c>&lt;App&gt;.app/Contents/Frameworks/</c>
    /// and uses the standard external message pump.
    ///
    /// <para>Call AFTER <c>BuildAvaloniaApp().SetupWithLifetime(...)</c>
    /// — Avalonia must initialise first on macOS so libAvaloniaNative's
    /// Obj-C classes are registered before CEF loads. The pump timer is
    /// installed by <see cref="UseExclr8Cef(AppBuilder)"/>, which should
    /// be chained on the <c>AppBuilder</c>.</para>
    /// </summary>
    /// <param name="args">Command-line args; <c>null</c> = use <see cref="Environment.GetCommandLineArgs"/>.</param>
    /// <param name="settings">Optional CEF init settings (cache path, user-agent, log file, etc.).</param>
    /// <param name="helperName">Helper-bundle base name (e.g. <c>"MyApp Helper"</c>). <c>null</c> = auto-detect any <c>* Helper.app</c> under <c>Contents/Frameworks/</c>.</param>
    public static void InitializeForOsr(string[]? args = null, Cef.CefSettings? settings = null, string? helperName = null)
    {
        if (settings is not null) Cef.SetInitSettings(settings);
        var helperPath = LocateMacHelper(helperName);
        // SchedulePumpWork is a no-op — UseExclr8Cef's DispatcherTimer
        // drains CEF unconditionally at 16ms ticks. A coalescing version
        // that honours the delay would be lower-CPU; this matches what
        // the reference demos validated and works correctly.
        Cef.InitializeForOsr(
            args,
            helperPath,
            _ => { });
    }

    /// <summary>
    /// Chain on the Avalonia <see cref="AppBuilder"/> to wire CEF's
    /// message pump into the UI dispatcher. Runs <c>Cef.DoMessageLoopWork</c>
    /// every 16ms on the UI thread once Avalonia finishes setup.
    /// </summary>
    public static AppBuilder UseExclr8Cef(this AppBuilder builder) =>
        builder.AfterSetup(_ =>
        {
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Background,
                (_, _) => Cef.DoMessageLoopWork());
            timer.Start();
        });

    /// <summary>
    /// Resolve the macOS CEF Helper subprocess executable path inside
    /// the current <c>.app</c> bundle. Returns <c>null</c> off macOS or
    /// when no matching helper bundle is found.
    /// </summary>
    /// <param name="helperName">
    /// Exact helper base name (without <c>.app</c>). If <c>null</c>,
    /// picks the first <c>* Helper.app</c> in <c>Contents/Frameworks/</c>
    /// — Chromium's convention is one main helper plus per-process
    /// variants (<c>* Helper (GPU).app</c>, <c>(Renderer)</c>, etc.) that
    /// CEF derives from the main path automatically.
    /// </param>
    public static string? LocateMacHelper(string? helperName = null)
    {
        if (!OperatingSystem.IsMacOS()) return null;

        var frameworks = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "Frameworks"));
        if (!Directory.Exists(frameworks)) return null;

        if (helperName is not null)
        {
            var path = Path.Combine(frameworks, $"{helperName}.app",
                                     "Contents", "MacOS", helperName);
            return File.Exists(path) ? path : null;
        }

        // Look for a bundle whose name is "<X> Helper" — the main helper.
        // Skip per-process variants ("(GPU)", "(Renderer)", "(Plugin)",
        // …) — CEF derives those from the main helper at runtime.
        foreach (var dir in Directory.GetDirectories(frameworks, "*.app"))
        {
            var name = Path.GetFileNameWithoutExtension(dir);
            if (!name.EndsWith(" Helper", StringComparison.Ordinal)) continue;
            var exe = Path.Combine(dir, "Contents", "MacOS", name);
            if (File.Exists(exe)) return exe;
        }
        return null;
    }
}
