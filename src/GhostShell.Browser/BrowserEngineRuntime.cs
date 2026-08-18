using System.Diagnostics;
using Avalonia;
using Exclr8Cef;
using Exclr8Cef.WebView;

namespace GhostShell.Browser;

/// <summary>
/// Owns the one process-wide CEF runtime used by all browser surfaces.
/// </summary>
public static class BrowserEngineRuntime
{
    private const string ExpectedCefVersion = "150.0.9";
    private const string ExpectedChromiumVersion = "150.0.7871.46";
    private const string ExpectedShimVersion = "0.8.0-ghostshell.4";
    private static readonly object StateGate = new();
    private static bool _initialized;
    private static bool _shutdown;

    /// <summary>
    /// Lets CEF claim renderer/GPU/utility subprocess invocations before any
    /// GhostSHELL single-instance, storage, or UI initialization occurs.
    /// </summary>
    public static int ExecuteSubprocess() => Cef.ExecuteProcess();

    /// <summary>
    /// Adds CEF's external message pump to the Avalonia application builder.
    /// </summary>
    public static AppBuilder Configure(AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseExclr8Cef();
    }

    /// <summary>
    /// Initializes CEF after Avalonia setup. On macOS this ordering is required
    /// so Avalonia's Objective-C classes exist before the CEF framework loads.
    /// </summary>
    public static void Initialize(BrowserEngineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (StateGate)
        {
            if (_initialized)
            {
                return;
            }

            if (_shutdown)
            {
                throw new InvalidOperationException(
                    "CEF cannot be initialized again after shutdown.");
            }

            PreparePrivateDirectory(options.ProfileDirectory);
            PrepareProfileLayout(options.ProfileDirectory);
            PreparePrivateDirectory(
                Path.GetDirectoryName(options.LogFilePath)
                ?? throw new ArgumentException(
                    "The CEF log path must have a parent directory.",
                    nameof(options)));

            var helperPath = AvaloniaSetup.LocateMacHelper(
                "GhostSHELL Helper");
            if (OperatingSystem.IsMacOS() && helperPath is null)
            {
                throw new FileNotFoundException(
                    "The GhostSHELL CEF helper bundle is missing from "
                    + "Contents/Frameworks. Build or package a complete CEF runtime payload.");
            }

            var versions = Cef.GetVersions();
            ValidateVersions(versions);
            Cef.SetInitSettings(new Cef.CefSettings
            {
                // GhostSHELL browsers always receive an explicit profile
                // context. Keep CEF's otherwise-unused global context apart so
                // every user-visible partition can be released and cleared.
                CachePath = Path.Combine(options.ProfileDirectory, "runtime"),
                RootCachePath = options.ProfileDirectory,
                UserAgentProduct = $"GhostSHELL/{options.ProductVersion}",
                LogFile = options.LogFilePath,
                LogSeverity = Cef.CefLogSeverity.Warning,
                PersistSessionCookies = true,
                RemoteDebuggingPort = 0,
            });

            // Environment.GetCommandLineArgs includes argv[0]. This matters on
            // Linux, where --type=renderer must not accidentally become argv[0].
            Cef.InitializeForOsr(
                Environment.GetCommandLineArgs(),
                helperPath,
                _ => { });
            _initialized = true;
        }
    }

    /// <summary>
    /// Closes every browser and continues pumping on the main thread until CEF
    /// confirms OnBeforeClose for each one, then performs process shutdown.
    /// </summary>
    /// <returns>False when browser close confirmation timed out.</returns>
    public static bool Shutdown(TimeSpan? timeout = null)
    {
        lock (StateGate)
        {
            if (!_initialized || _shutdown)
            {
                return true;
            }

            var closeTimeout = timeout ?? TimeSpan.FromSeconds(10);
            if (closeTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            foreach (var browser in Cef.Browsers.ToArray())
            {
                browser.Close(force: true);
            }

            var elapsed = Stopwatch.StartNew();
            while (Cef.Browsers.Any() && elapsed.Elapsed < closeTimeout)
            {
                Cef.DoMessageLoopWork();
                Thread.Sleep(TimeSpan.FromMilliseconds(5));
            }

            if (Cef.Browsers.Any())
            {
                Console.Error.WriteLine(
                    "GhostSHELL did not receive CEF close confirmation for "
                    + $"{Cef.Browsers.Count()} browser(s) within {closeTimeout}.");
                return false;
            }

            Cef.Shutdown();
            _shutdown = true;
            _initialized = false;
            return true;
        }
    }

    internal static void ValidateVersions(CefVersions versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (!string.Equals(
                versions.Shim,
                ExpectedShimVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                versions.Cef,
                ExpectedCefVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                versions.Chromium,
                ExpectedChromiumVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The staged CEF runtime does not match the managed binding. "
                + $"Expected shim {ExpectedShimVersion} / CEF "
                + $"{ExpectedCefVersion} / Chromium "
                + $"{ExpectedChromiumVersion}, found CEF {versions.Cef} / "
                + $"Chromium {versions.Chromium} / shim {versions.Shim}.");
        }
    }

    private static void PreparePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    internal static void PrepareProfileLayout(string rootDirectory)
    {
        var runtimeDirectory = Path.Combine(rootDirectory, "runtime");
        var globalProfilesDirectory = Path.Combine(
            rootDirectory,
            "profiles",
            "global");
        var globalDirectory = Path.Combine(
            globalProfilesDirectory,
            "local");
        PreparePrivateDirectory(runtimeDirectory);
        PreparePrivateDirectory(globalProfilesDirectory);

        // Builds before browser profiles stored the shared Chromium profile
        // in the Default child of the CEF root. The explicit request context
        // uses its cache path itself as the profile directory, so move Default
        // to that exact path (not below it) exactly once.
        var legacyDefault = Path.Combine(rootDirectory, "Default");
        if (Directory.Exists(legacyDefault)
            && !Directory.Exists(globalDirectory))
        {
            Directory.Move(legacyDefault, globalDirectory);
        }

        PreparePrivateDirectory(globalDirectory);
    }
}

public sealed record BrowserEngineRuntimeOptions
{
    public BrowserEngineRuntimeOptions(
        string profileDirectory,
        string logFilePath,
        string productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ProfileDirectory = Path.GetFullPath(profileDirectory);
        LogFilePath = Path.GetFullPath(logFilePath);
        ProductVersion = productVersion;
    }

    public string ProfileDirectory { get; }

    public string LogFilePath { get; }

    public string ProductVersion { get; }

    public string BrowserProfilesDirectory => Path.Combine(
        ProfileDirectory,
        "profiles");
}
