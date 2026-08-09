using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Exclr8Cef.WebView.Demo;

public partial class MainWindow : Window
{
    /// <summary>
    /// Backing collection for the host-side event console pane. Every CEF
    /// callback we surface to .NET (navigation, title, loading, cursor, …)
    /// pushes a row through <see cref="LogEvent"/>. As phases land, more
    /// categories (Console, Load, Download, Dialog, …) will appear here.
    /// </summary>
    public ObservableCollection<EventEntry> Events { get; } = new();

    private const int MaxEvents = 500;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        // Load the test page through our custom app:// scheme — proves
        // the resource handler delivers a top-level navigation cleanly.
        Browser.NavigateToUrl("app://demo/test-page.html");

        Browser.PropertyChanged += OnBrowserPropertyChanged;

        // Subscribe to the underlying CefBrowser's event stack as soon as it
        // exists (creation is lazy — happens on the first arrange with size).
        Browser.AttachedToVisualTree += OnBrowserAttachedForEvents;

        // Tunnel-phase handler so we can claim the click for hit-test mode
        // before the WebView forwards it to CEF.
        Browser.AddHandler(InputElement.PointerPressedEvent, OnBrowserPointerForHitTest,
            RoutingStrategies.Tunnel);

        AddressBox.Text = Browser.Url;
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;

        LogEvent("init", "Demo started");
    }

    private bool _hitTestMode;

    private async void OnBrowserPointerForHitTest(object? sender, PointerPressedEventArgs e)
    {
        if (!_hitTestMode) return;
        if (Browser.Browser is not { } b) return;
        var p = e.GetCurrentPoint(Browser);
        int x = (int)p.Position.X, y = (int)p.Position.Y;
        e.Handled = true;  // suppress CEF forwarding
        try
        {
            var hit = await b.HitTestAtAsync(x, y);
            if (hit is null) LogEvent("hit-test", $"({x},{y}): no element");
            else LogEvent("hit-test", $"({x},{y}): <{hit.Tag}> id={hit.Id ?? "-"} text='{hit.Text}' rect=({hit.X:F0},{hit.Y:F0} {hit.Width:F0}x{hit.Height:F0})");
        }
        catch (Exception ex)
        {
            LogEvent("hit-test", $"error: {ex.Message}");
        }
    }

    private void OnHitTestModeClick(object? sender, RoutedEventArgs e)
    {
        _hitTestMode = !_hitTestMode;
        HitTestButton.Content = _hitTestMode ? "Hit-test mode ✓" : "Hit-test mode";
        LogEvent("hit-test", _hitTestMode ? "enabled — click to probe" : "disabled");
    }

    private void OnEmbeddedClick(object? sender, RoutedEventArgs e) => SpawnDemoMode("--mode=embedded", "embedded");
    private void SpawnDemoMode(string modeArg, string logTag)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            if (OperatingSystem.IsMacOS())
            {
                string bundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
                psi = new System.Diagnostics.ProcessStartInfo { FileName = "open", UseShellExecute = false };
                psi.ArgumentList.Add("-n"); psi.ArgumentList.Add("-a"); psi.ArgumentList.Add(bundle);
                psi.ArgumentList.Add("--args"); psi.ArgumentList.Add(modeArg);
            }
            else
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(modeArg);
            }
            System.Diagnostics.Process.Start(psi);
            LogEvent(logTag, $"spawned {modeArg}");
        }
        catch (Exception ex) { LogEvent(logTag, $"failed: {ex.Message}"); }
    }

    private void OnWindowedClick(object? sender, RoutedEventArgs e)
    {
        // Spawn the same binary as a separate process with --mode=windowed.
        // CefSettings.windowless_rendering_enabled is process-wide, so we
        // can't mix OSR + Chrome-runtime windowed in one process. The
        // windowed mode uses a separate cef-cache-windowed/ dir so it
        // doesn't fight us for the OSR cache lock.
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            if (OperatingSystem.IsMacOS())
            {
                // Resolve the .app bundle: AppContext.BaseDirectory is
                // <bundle>/Contents/MacOS — go up two levels to the bundle.
                string bundle = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", ".."));
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-n");          // always new instance
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(bundle);
                psi.ArgumentList.Add("--args");
                psi.ArgumentList.Add("--mode=windowed");
            }
            else
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--mode=windowed");
            }
            System.Diagnostics.Process.Start(psi);
            LogEvent("windowed", "spawned --mode=windowed");
        }
        catch (Exception ex)
        {
            LogEvent("windowed", $"failed: {ex.Message}");
        }
    }

    private void OnCapturePngClick(object? sender, RoutedEventArgs e)
    {
        if (Browser.Browser is not { } b) { LogEvent("capture", "no browser"); return; }
        b.EnableFrameCapture(true);
        if (!b.TryCaptureLastFrame(out var bgra, out int w, out int h) || bgra is null)
        {
            LogEvent("capture", "no frame yet — try again");
            return;
        }
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"exclr8cef-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        try
        {
            using var bmp = new WriteableBitmap(
                new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var locked = bmp.Lock())
            {
                Marshal.Copy(bgra, 0, locked.Address, bgra.Length);
            }
            bmp.Save(path);
            LogEvent("capture", $"saved {w}x{h} → {path}");
        }
        catch (Exception ex)
        {
            LogEvent("capture", $"failed: {ex.Message}");
        }
    }

    // Hook events on the underlying tech-neutral CefBrowser. Done lazily
    // because the WebView creates its CefBrowser on first arrange.
    private bool _browserEventsHooked;
    private void OnBrowserAttachedForEvents(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(TryHookBrowserEvents,
            Avalonia.Threading.DispatcherPriority.Background);
    }
    private void TryHookBrowserEvents()
    {
        if (_browserEventsHooked) return;
        if (Browser.Browser is not { } b)
        {
            // Try again after the next layout pass.
            Avalonia.Threading.Dispatcher.UIThread.Post(TryHookBrowserEvents,
                Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        _browserEventsHooked = true;
        b.Initialized += (_, _) => LogEvent("ready", $"CefBrowser #{b.Id} initialised");
        b.ConsoleMessage        += OnBrowserConsoleMessage;
        b.LoadStart             += OnBrowserLoadStart;
        b.LoadEnd               += OnBrowserLoadEnd;
        b.LoadError             += OnBrowserLoadError;
        b.LoadingProgress       += OnBrowserLoadingProgress;
        b.StatusMessage         += OnBrowserStatusMessage;
        b.TooltipChanged        += OnBrowserTooltip;
        b.FaviconChanged        += OnBrowserFavicon;
        b.FullscreenModeChanged += OnBrowserFullscreen;
        b.ScrollOffsetChanged   += OnBrowserScrollOffset;
        b.AutoResize            += OnBrowserAutoResize;
        b.JsDialog              += OnBrowserJsDialog;
        b.FileDialog            += OnBrowserFileDialog;
        b.ContextMenu           += OnBrowserContextMenu;
        b.DownloadStarting      += OnBrowserDownloadStarting;
        b.DownloadProgress      += OnBrowserDownloadProgress;
        b.AuthRequest           += OnBrowserAuthRequest;
        b.FindResult            += OnBrowserFindResult;
        b.RenderProcessGone     += OnBrowserRenderProcessGone;
        b.ResourceRequest       += OnBrowserResourceRequest;
        b.JsInvoke              += OnBrowserJsInvoke;
        b.AccessibilityTreeChange     += OnBrowserA11yTree;
        b.AccessibilityLocationChange += OnBrowserA11yLocation;
        b.DragStarted           += OnBrowserDragStarted;
        b.PermissionRequest     += OnBrowserPermissionRequest;
        b.MediaAccessRequest    += OnBrowserMediaAccessRequest;
        b.BeforePopup           += OnBrowserBeforePopup;
        b.CertError             += OnBrowserCertError;

        // Always-on frame capture so the Capture PNG toolbar button has a
        // snapshot to save the first time it's clicked. ~3MB resident.
        b.EnableFrameCapture(true);
    }

    private async void OnBrowserCertError(object? sender, Exclr8Cef.CertErrorEventArgs e)
    {
        try
        {
            LogEvent("cert", $"{e.ErrorCode} for {e.RequestUrl} subject={e.SubjectCommonName} issuer={e.IssuerCommonName}");
            var result = await Exclr8Cef.WebView.Demo.JsDialogWindow.ShowAsync(
                this, Exclr8Cef.Cef.JsDialogType.Confirm,
                $"TLS error {e.ErrorCode} for {e.RequestUrl}\nSubject: {e.SubjectCommonName}\nIssuer: {e.IssuerCommonName}\n\nProceed anyway?",
                defaultPromptText: "");
            if (result.Accepted) { e.Proceed(); LogEvent("cert", "→ Proceed"); }
            else                 { e.Cancel();  LogEvent("cert", "→ Cancel");  }
        }
        catch (Exception ex)
        {
            LogEvent("cert", $"handler error → Cancel: {ex.Message}");
        }
        finally
        {
            e.Cancel();
        }
    }

    private void OnBrowserBeforePopup(object? sender, Exclr8Cef.BeforePopupEventArgs e)
    {
        LogEvent("popup", $"{e.Disposition} → {e.TargetUrl} (user_gesture={e.UserGesture})");
        // Open the URL in the system's default browser. CEF has already
        // cancelled the popup at this point (subscriber-present → cancel).
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.TargetUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogEvent("popup", $"open failed: {ex.Message}");
        }
    }

    private async void OnBrowserPermissionRequest(object? sender, Exclr8Cef.PermissionRequestEventArgs e)
    {
        try
        {
            LogEvent("permission", $"prompt: {e.RequestedPermissions} from {e.Origin}");
            var result = await Exclr8Cef.WebView.Demo.JsDialogWindow.ShowAsync(
                this, Exclr8Cef.Cef.JsDialogType.Confirm,
                $"{e.Origin} wants permission for: {e.RequestedPermissions}",
                defaultPromptText: "");
            var pr = result.Accepted ? Exclr8Cef.Cef.PermissionResult.Accept : Exclr8Cef.Cef.PermissionResult.Deny;
            LogEvent("permission", $"→ {pr}");
            e.Continue(pr);
        }
        catch (Exception ex)
        {
            LogEvent("permission", $"handler error → Deny: {ex.Message}");
        }
        finally
        {
            // Idempotent Deny — if a path above already resolved, this is a no-op.
            e.Deny();
        }
    }

    private async void OnBrowserMediaAccessRequest(object? sender, Exclr8Cef.MediaAccessRequestEventArgs e)
    {
        try
        {
            LogEvent("media", $"getUserMedia: {e.RequestedPermissions} from {e.Origin}");
            var result = await Exclr8Cef.WebView.Demo.JsDialogWindow.ShowAsync(
                this, Exclr8Cef.Cef.JsDialogType.Confirm,
                $"{e.Origin} wants camera/mic access: {e.RequestedPermissions}",
                defaultPromptText: "");
            if (result.Accepted) { e.AllowAll(); LogEvent("media", "→ Allow"); }
            else                 { e.Deny();     LogEvent("media", "→ Deny");  }
        }
        catch (Exception ex)
        {
            LogEvent("media", $"handler error → Deny: {ex.Message}");
        }
        finally
        {
            e.Deny();
        }
    }

    private void OnBrowserDragStarted(object? sender, Exclr8Cef.DragStartedEventArgs e)
    {
        var summary = !string.IsNullOrEmpty(e.LinkUrl) ? $"link {e.LinkUrl}"
                    : e.FileNames.Count > 0          ? $"{e.FileNames.Count} file(s)"
                    : !string.IsNullOrEmpty(e.Text)  ? $"text '{Truncate(e.Text, 32)}'"
                    : "(empty)";
        LogEvent("drag-out", $"page start drag: {summary} ops={e.AllowedOperations}");
        // Leave Handled=false → shim self-targets (internal page DnD).
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // Bucket request logs so the event console stays usable on busy pages —
    // log main_frame / sub_frame / xhr / fetch traffic in full, the rest as
    // a short summary per resource type per page-load batch.
    private int _resourceRequestLogged;
    private void OnBrowserJsInvoke(object? sender, Exclr8Cef.JsInvokeEventArgs e)
    {
        LogEvent("js-invoke", $"{e.Method}({e.ArgsJson})");
        switch (e.Method)
        {
            case "a11y" when Browser.Browser is { } b:
                b.SetAccessibilityEnabled(e.ArgsJson == "on");
                e.Reply($"\"a11y {e.ArgsJson}\"");
                break;
            case "capture":
                Dispatcher.UIThread.Post(() => OnCapturePngClick(this, new RoutedEventArgs()));
                e.Reply("\"capture queued\"");
                break;
            case "ping":
                // Echo the args back — useful for verifying the round-trip.
                e.Reply(string.IsNullOrEmpty(e.ArgsJson) ? "\"pong\"" : e.ArgsJson);
                break;
            case "time":
                e.Reply($"\"{DateTime.UtcNow:O}\"");
                break;
            default:
                e.ReplyError($"unknown method '{e.Method}'");
                break;
        }
    }

    // The a11y tree fires for every layout update. Showing the full JSON in
    // the event console would drown everything else — log a small summary.
    private void OnBrowserA11yTree(object? sender, string json)
        => LogEvent("a11y", $"tree update ({json.Length}B)");

    private void OnBrowserA11yLocation(object? sender, string json)
        => LogEvent("a11y", $"location update ({json.Length}B)");

    private void OnBrowserResourceRequest(object? sender, Exclr8Cef.ResourceRequestEventArgs e)
    {
        // Demonstrate header injection: append a marker header so hosts /
        // remote endpoints can see that the request went through our hook.
        var newHeaders = e.CurrentHeaders;
        if (!string.IsNullOrEmpty(newHeaders)) newHeaders += "\n";
        newHeaders += "X-Exclr8Cef-Demo: hello";

        // Log a sample so the event console isn't flooded.
        if (e.Type == Exclr8Cef.Cef.ResourceType.MainFrame
            || e.Type == Exclr8Cef.Cef.ResourceType.Xhr
            || _resourceRequestLogged++ < 5)
        {
            LogEvent("request", $"{e.Type} {e.Method} {e.Url}");
        }

        e.Continue(newHeaders);
    }

    private void OnBrowserRenderProcessGone(object? sender, Exclr8Cef.RenderProcessGoneEventArgs e)
    {
        LogEvent("renderer", $"{e.Status} code={e.ErrorCode} {e.ErrorString}");
        // Recovery: reload after a short delay so the user can see the
        // crashed state, then the page comes back.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(800);
            Browser.Browser?.Reload();
        });
    }

    private async void OnBrowserAuthRequest(object? sender, Exclr8Cef.AuthRequestEventArgs e)
    {
        LogEvent("auth", $"{e.Scheme.ToUpperInvariant()} {e.Host}:{e.Port} realm=\"{e.Realm}\" proxy={e.IsProxy}");
        var result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => AuthDialogWindow.ShowAsync(this, e.Scheme, e.Host, e.Port, e.Realm));
        if (result is { } creds) e.Continue(creds.Username, creds.Password);
        else e.Cancel();
    }

    private void OnBrowserFindResult(object? sender, Exclr8Cef.FindResultEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            FindMatchCount.Text = e.Count == 0 ? "no matches"
                                : $"{e.ActiveMatchOrdinal} / {e.Count}";
        });
    }

    private async void OnBrowserDownloadStarting(object? sender, Exclr8Cef.DownloadStartingEventArgs e)
    {
        LogEvent("download", $"starting #{e.DownloadId}: {e.SuggestedName} ({e.MimeType}, {e.TotalBytes}B) ← {e.Url}");
        var sp = StorageProvider;
        if (sp is null) { e.Cancel(); return; }
        var picked = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save download",
            SuggestedFileName = e.SuggestedName,
        });
        if (picked is null) { e.Cancel(); return; }
        e.Continue(picked.Path.LocalPath);
    }

    // Bucket download-progress events so the log stays readable.
    private readonly Dictionary<int, int> _lastDownloadBucket = new();
    private void OnBrowserDownloadProgress(object? sender, Exclr8Cef.DownloadProgressEventArgs e)
    {
        // Log every 10% bucket and the final state transition.
        int bucket = e.PercentComplete < 0 ? -1 : e.PercentComplete / 10;
        if (e.State == Exclr8Cef.Cef.DownloadState.InProgress)
        {
            if (_lastDownloadBucket.TryGetValue(e.DownloadId, out var b) && b == bucket) return;
            _lastDownloadBucket[e.DownloadId] = bucket;
            LogEvent("download", $"#{e.DownloadId}: {e.PercentComplete}% ({e.ReceivedBytes}/{e.TotalBytes}B, {e.CurrentSpeedBytesPerSec}B/s)");
        }
        else
        {
            _lastDownloadBucket.Remove(e.DownloadId);
            LogEvent("download", $"#{e.DownloadId}: {e.State} → {e.FullPath}");
        }
    }

    private void OnBrowserContextMenu(object? sender, Exclr8Cef.ContextMenuEventArgs e)
    {
        LogEvent("contextmenu", $"({e.X},{e.Y}) {e.Items.Count} items");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var menu = new Avalonia.Controls.MenuFlyout();
            bool resolved = false;
            foreach (var it in e.Items)
            {
                if (it.IsSeparator)
                {
                    menu.Items.Add(new Avalonia.Controls.Separator());
                    continue;
                }
                var label = it.Label.Replace("&", "");
                var mi = new Avalonia.Controls.MenuItem { Header = label };
                int id = it.CommandId;
                mi.Click += (_, _) => { resolved = true; e.Continue(id); };
                menu.Items.Add(mi);
            }
            // Dismiss without selection = cancel.
            menu.Closed += (_, _) => { if (!resolved) e.Cancel(); };

            // Position at click coords. ShowAt takes a placement target;
            // we use the WebView itself with a small Popup placement
            // workaround: an invisible Border at the click point.
            var anchor = new Avalonia.Controls.Border
            {
                Width = 1, Height = 1,
                Margin = new Avalonia.Thickness(e.X, e.Y, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            // Add anchor to the WebView's parent panel temporarily.
            if (Browser.Parent is Avalonia.Controls.Panel panel)
            {
                panel.Children.Add(anchor);
                menu.ShowAt(anchor);
                menu.Closed += (_, _) => panel.Children.Remove(anchor);
            }
            else
            {
                menu.ShowAt(Browser);
            }
        });
    }

    private async void OnBrowserFileDialog(object? sender, Exclr8Cef.FileDialogEventArgs e)
    {
        LogEvent("file-dialog", $"{e.Mode} title=\"{e.Title}\" filters=[{string.Join(",", e.AcceptFilters)}]");

        // Map Avalonia filters from CEF's MIME / glob list. Crude — we
        // accept any pattern as a single named filter; real apps would
        // map per-type with sensible names.
        var fileTypeFilter = e.AcceptFilters.Count == 0
            ? null
            : new[] { new Avalonia.Platform.Storage.FilePickerFileType("Accepted")
              { Patterns = [.. e.AcceptFilters.Where(p => p.StartsWith("*") || p.Contains('.'))
                                              .DefaultIfEmpty("*.*")] } };

        var sp = StorageProvider;
        if (sp is null) { e.Cancel(); return; }

        switch (e.Mode)
        {
            case Exclr8Cef.Cef.FileDialogMode.Open:
            case Exclr8Cef.Cef.FileDialogMode.OpenMultiple:
            {
                var opts = new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = e.Title,
                    AllowMultiple = e.Mode == Exclr8Cef.Cef.FileDialogMode.OpenMultiple,
                    FileTypeFilter = fileTypeFilter,
                };
                var picked = await sp.OpenFilePickerAsync(opts);
                if (picked.Count == 0) { e.Cancel(); return; }
                e.Continue(picked.Select(f => f.Path.LocalPath).ToArray());
                break;
            }
            case Exclr8Cef.Cef.FileDialogMode.OpenFolder:
            {
                var opts = new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = e.Title };
                var picked = await sp.OpenFolderPickerAsync(opts);
                if (picked.Count == 0) { e.Cancel(); return; }
                e.Continue(picked[0].Path.LocalPath);
                break;
            }
            case Exclr8Cef.Cef.FileDialogMode.Save:
            {
                var opts = new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = e.Title,
                    SuggestedFileName = string.IsNullOrEmpty(e.DefaultPath) ? null : System.IO.Path.GetFileName(e.DefaultPath),
                    FileTypeChoices = fileTypeFilter,
                };
                var picked = await sp.SaveFilePickerAsync(opts);
                if (picked is null) { e.Cancel(); return; }
                e.Continue(picked.Path.LocalPath);
                break;
            }
            default:
                e.Cancel();
                break;
        }
    }

    private async void OnBrowserJsDialog(object? sender, Exclr8Cef.JsDialogEventArgs e)
    {
        LogEvent("js-dialog", $"{e.Type}: {e.Message}");
        // Show the host's native dialog. The handler must call Continue or
        // Cancel exactly once; the args object guards against double-resolve.
        var result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => JsDialogWindow.ShowAsync(this, e.Type, e.Message, e.DefaultPromptText));
        if (result.Accepted) e.Continue(result.PromptText);
        else                 e.Cancel();
    }

    // Scroll fires very often — bucket to 50px so the log stays useful.
    private int _lastScrollBucketY = -1;
    private void OnBrowserScrollOffset(object? sender, Exclr8Cef.ScrollOffsetEventArgs e)
    {
        int bucket = (int)(e.Y / 50);
        if (bucket == _lastScrollBucketY) return;
        _lastScrollBucketY = bucket;
        LogEvent("scroll", $"x={e.X:F0} y={e.Y:F0}");
    }

    private void OnBrowserAutoResize(object? sender, Exclr8Cef.AutoResizeEventArgs e)
        => LogEvent("autoresize", $"{e.Width} × {e.Height}");

    private void OnBrowserStatusMessage(object? sender, string text)
    {
        // OnStatusMessage fires frequently (every mouse-move over a link).
        // Suppress empty + duplicate-consecutive values to keep the event
        // console useful.
        if (text == _lastStatus) return;
        _lastStatus = text;
        LogEvent("status", string.IsNullOrEmpty(text) ? "(cleared)" : text);
    }
    private string? _lastStatus;

    private void OnBrowserTooltip(object? sender, string text)
        => LogEvent("tooltip", string.IsNullOrEmpty(text) ? "(cleared)" : text);

    private static readonly System.Net.Http.HttpClient s_faviconHttp = new();

    private async void OnBrowserFavicon(object? sender, string url)
    {
        LogEvent("favicon", url);
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            byte[]? bytes = null;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // data:[<media>][;base64],<data>
                var comma = url.IndexOf(',');
                if (comma > 0)
                {
                    var header = url[..comma];
                    var payload = url[(comma + 1)..];
                    if (header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                    {
                        bytes = Convert.FromBase64String(payload);
                    }
                    else
                    {
                        bytes = System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                    }
                }
            }
            else if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var resp = await s_faviconHttp.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                    bytes = await resp.Content.ReadAsByteArrayAsync();
            }

            if (bytes is null || bytes.Length == 0) return;

            // Bitmap decode supports PNG/JPEG/BMP/GIF — not SVG/ICO. Wrap in a
            // try/catch so unsupported formats just leave the previous icon up.
            using var stream = new MemoryStream(bytes);
            var bmp = new Avalonia.Media.Imaging.Bitmap(stream);
            FaviconImage.Source = bmp;
            FaviconImage.IsVisible = true;
        }
        catch
        {
            // Unsupported format / fetch error — leave the previous icon.
        }
    }

    private WindowState? _savedWindowState;
    private Avalonia.Controls.GridLength? _savedConsoleHeight;

    private void OnBrowserFullscreen(object? sender, bool fullscreen)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // OnFullscreenModeChange is the host's cue to actually expand /
            // restore the window. In OSR mode CEF doesn't own a window, so
            // visible fullscreen is the host's responsibility:
            //  - WindowState.FullScreen so the OS hides the title bar
            //  - Hide every chrome row (branding, address bar, status, event
            //    console pane) so the WebView fills the entire window
            //  - Restore everything on exit
            if (fullscreen)
            {
                _savedWindowState ??= WindowState;
                _savedConsoleHeight ??= MainGrid.RowDefinitions[2].Height;

                WindowState = WindowState.FullScreen;
                BrandingStrip.IsVisible    = false;
                AddressBar.IsVisible       = false;
                StatusStrip.IsVisible      = false;
                ConsoleSplitter.IsVisible  = false;
                EventConsolePane.IsVisible = false;
                MainGrid.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(0);
            }
            else
            {
                WindowState = _savedWindowState ?? WindowState.Normal;
                BrandingStrip.IsVisible    = true;
                AddressBar.IsVisible       = true;
                StatusStrip.IsVisible      = true;
                ConsoleSplitter.IsVisible  = true;
                EventConsolePane.IsVisible = true;
                MainGrid.RowDefinitions[2].Height = _savedConsoleHeight ?? new Avalonia.Controls.GridLength(220);
                _savedWindowState = null;
                _savedConsoleHeight = null;
            }
        });
        LogEvent("fullscreen", fullscreen ? "entered" : "exited");
    }

    private static string FrameTag(bool isMainFrame) => isMainFrame ? "[main] " : "[sub]  ";

    private void OnBrowserLoadStart(object? sender, Exclr8Cef.LoadStartEventArgs e)
        => LogEvent("load-start", FrameTag(e.IsMainFrame) + e.Url);

    private void OnBrowserLoadEnd(object? sender, Exclr8Cef.LoadEndEventArgs e)
        => LogEvent("load-end", FrameTag(e.IsMainFrame) + (e.HttpStatusCode == 0 ? "" : $"HTTP {e.HttpStatusCode} · ") + e.Url);

    private void OnBrowserLoadError(object? sender, Exclr8Cef.LoadErrorEventArgs e)
    {
        // ERR_ABORTED is fired on every intentional navigation cancel (incl.
        // every reload-during-load) — not really an error worth surfacing as
        // such. Tag it differently.
        var cat = e.ErrorCode == Exclr8Cef.Cef.CefErrorCode.Aborted ? "load-abort" : "load-err";
        LogEvent(cat, FrameTag(e.IsMainFrame) + $"{e.ErrorCode} ({(int)e.ErrorCode}): {e.ErrorText} · {e.FailedUrl}");
    }

    private void OnBrowserLoadingProgress(object? sender, double progress)
    {
        // Throttle: only log on visible-change boundaries (10% buckets) to
        // avoid spamming the event console with dozens of rows per load.
        int bucket = (int)Math.Round(progress * 10);
        if (bucket == _lastProgressBucket) return;
        _lastProgressBucket = bucket;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = progress * 100;
            ProgressBar.IsVisible = progress > 0 && progress < 1;
        });
        LogEvent("progress", $"{progress:P0}");
    }

    private int _lastProgressBucket = -1;

    private void OnBrowserConsoleMessage(object? sender, Exclr8Cef.ConsoleMessageEventArgs e)
    {
        // Truncate source URL to the last path segment for readability.
        var src = e.Source;
        var slash = src.LastIndexOf('/');
        if (slash >= 0 && slash < src.Length - 1) src = src[(slash + 1)..];
        var loc = string.IsNullOrEmpty(src) ? "" : $"  ({src}:{e.Line})";
        LogEvent("console." + e.Level.ToString().ToLowerInvariant(), e.Message + loc);
    }

    private void LogEvent(string category, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => LogEvent(category, message));
            return;
        }

        Events.Add(new EventEntry(DateTime.Now.ToString("HH:mm:ss.fff"), category, message));
        while (Events.Count > MaxEvents) Events.RemoveAt(0);

        EventScroller.ScrollToEnd();
    }

    private void OnClearEventsClick(object? sender, RoutedEventArgs e) => Events.Clear();

    // ---- Find-in-page bar ---------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var accel = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.F && (e.KeyModifiers & accel) != 0)
        {
            ShowFindBar();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && FindBar.IsVisible)
        {
            HideFindBar();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void ShowFindBar()
    {
        FindBar.IsVisible = true;
        FindMatchCount.Text = "";
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void HideFindBar()
    {
        FindBar.IsVisible = false;
        FindBox.Text = "";
        FindMatchCount.Text = "";
        Browser.Browser?.StopFinding(clearSelection: true);
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            var hasShift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            DoFind(forward: !hasShift, findNext: !string.IsNullOrEmpty(_lastFindQuery)
                                                 && FindBox.Text == _lastFindQuery);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { HideFindBar(); e.Handled = true; }
    }

    private string _lastFindQuery = "";
    private void DoFind(bool forward, bool findNext)
    {
        var q = FindBox.Text ?? "";
        if (string.IsNullOrEmpty(q))
        {
            Browser.Browser?.StopFinding(clearSelection: true);
            FindMatchCount.Text = "";
            return;
        }
        Browser.Browser?.Find(q, forward: forward, matchCase: false, findNext: findNext);
        _lastFindQuery = q;
        LogEvent("find", $"\"{q}\" forward={forward} next={findNext}");
    }

    private void OnFindPrevClick(object? sender, RoutedEventArgs e) => DoFind(false, true);
    private void OnFindNextClick(object? sender, RoutedEventArgs e) => DoFind(true, true);
    private void OnFindCloseClick(object? sender, RoutedEventArgs e) => HideFindBar();

    private void OnBrowserPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Exclr8Cef.WebView.WebView.UrlProperty)
        {
            var url = e.GetNewValue<string>();
            if (AddressBox.Text != url) AddressBox.Text = url;
            LogEvent("url", url ?? "");
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.TitleProperty)
        {
            var title = e.GetNewValue<string>();
            TitleText.Text = string.IsNullOrEmpty(title) ? "(loading…)" : title;
            Title = string.IsNullOrEmpty(title)
                ? "Exclr8CEF Avalonia Demo"
                : $"{title} — Exclr8CEF Avalonia Demo";
            LogEvent("title", title ?? "");
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.IsLoadingProperty)
        {
            var loading = e.GetNewValue<bool>();
            ReloadButton.Content = loading ? "✕" : "⟳";
            StatusText.Text = loading ? "Loading…" : "";
            LogEvent("loading", loading ? "started" : "finished");
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.CanGoBackProperty)
        {
            BackButton.IsEnabled = e.GetNewValue<bool>();
        }
        else if (e.Property == Exclr8Cef.WebView.WebView.CanGoForwardProperty)
        {
            ForwardButton.IsEnabled = e.GetNewValue<bool>();
        }
    }

    // Toolbar handlers route through the underlying CefBrowser exposed by
    // the control. The control deliberately doesn't ship LoadUrl/GoBack/
    // ShowDevTools/Zoom-style conveniences — toolbars belong to consumer
    // host code, not the control surface.
    private void OnBackClick(object? sender, RoutedEventArgs e) => Browser.Browser?.GoBack();
    private void OnForwardClick(object? sender, RoutedEventArgs e) => Browser.Browser?.GoForward();
    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (Browser.Browser is not { } b) return;
        if (Browser.IsLoading) b.StopLoad();
        else b.Reload();
    }
    private void OnStopClick(object? sender, RoutedEventArgs e) => Browser.Browser?.StopLoad();
    private void OnDevToolsClick(object? sender, RoutedEventArgs e) => Browser.Browser?.ShowDevTools();

    private void OnIsolatedClick(object? sender, RoutedEventArgs e)
    {
        var window = new IsolatedSessionWindow();
        window.Show(this);
    }

    private const double ZoomStep = 0.5;
    private void OnZoomInClick(object? sender, RoutedEventArgs e)
    {
        if (Browser.Browser is { } b) { b.ZoomLevel += ZoomStep; UpdateZoomUi(); }
    }
    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        if (Browser.Browser is { } b) { b.ZoomLevel -= ZoomStep; UpdateZoomUi(); }
    }
    private void OnZoomResetClick(object? sender, RoutedEventArgs e)
    {
        if (Browser.Browser is { } b) { b.ZoomLevel = 0; UpdateZoomUi(); }
    }

    private void UpdateZoomUi()
    {
        if (Browser.Browser is not { } b) return;
        // Disable In/Out at the zoom limits using CanZoom; show the current
        // percentage on the reset button.
        ZoomInButton.IsEnabled = b.CanZoomIn;
        ZoomOutButton.IsEnabled = b.CanZoomOut;
        ZoomResetButton.Content = $"{Math.Round(Math.Pow(1.2, b.ZoomLevel) * 100)}%";
    }

    private async void OnRunJsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Browser.Browser is not { } b)
            {
                StatusText.Text = "No browser yet."; return;
            }
            var result = await b.EvaluateJavaScriptAsync(
                "({ title: document.title, h1: document.querySelector('h1')?.innerText, " +
                "buttonText: document.querySelector('button')?.innerText, " +
                "url: location.href, " +
                "now: new Date().toISOString() })");
            StatusText.Text = $"JS → {result}";
            LogEvent("js", result);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"JS error: {ex.Message}";
            LogEvent("js-err", ex.Message);
        }
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            var url = AddressBox.Text ?? "";
            if (!url.Contains("://") && !url.StartsWith("data:") && !url.StartsWith("chrome:"))
            {
                url = "https://" + url;
            }
            Browser.NavigateToUrl(url);
            e.Handled = true;
        }
    }

    private async void OnSavePdfClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"exclr8cef-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        StatusText.Text = $"Writing {path}…";
        SavePdfButton.IsEnabled = false;
        try
        {
            bool ok = Browser.Browser is { } b && await b.PrintToPdfAsync(path);
            StatusText.Text = ok ? $"Saved: {path}" : "PDF export failed.";
            LogEvent("pdf", ok ? path : "failed");
        }
        finally
        {
            SavePdfButton.IsEnabled = true;
        }
    }
}

/// <summary>A single row in the event console.</summary>
public sealed record EventEntry(string Time, string Category, string Message);

/// <summary>
/// Maps an event category to a coloured badge background. Adding a new
/// category? Add a colour here; unmapped categories fall back to the
/// neutral pill colour.
/// </summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, IBrush> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["init"]            = SolidColorBrush.Parse("#94e2d5"),
        ["url"]             = SolidColorBrush.Parse("#89b4fa"),
        ["title"]           = SolidColorBrush.Parse("#cba6f7"),
        ["loading"]         = SolidColorBrush.Parse("#f9e2af"),
        ["js"]              = SolidColorBrush.Parse("#a6e3a1"),
        ["js-err"]          = SolidColorBrush.Parse("#f38ba8"),
        ["pdf"]             = SolidColorBrush.Parse("#fab387"),
        ["console.verbose"] = SolidColorBrush.Parse("#6c7086"),
        ["console.info"]    = SolidColorBrush.Parse("#a6e3a1"),
        ["console.warning"] = SolidColorBrush.Parse("#f9e2af"),
        ["console.error"]   = SolidColorBrush.Parse("#f38ba8"),
        ["console.fatal"]   = SolidColorBrush.Parse("#f38ba8"),
        ["console.default"] = SolidColorBrush.Parse("#a6adc8"),
        ["load-start"]      = SolidColorBrush.Parse("#74c7ec"),
        ["load-end"]        = SolidColorBrush.Parse("#a6e3a1"),
        ["load-err"]        = SolidColorBrush.Parse("#f38ba8"),
        ["load-abort"]      = SolidColorBrush.Parse("#6c7086"),
        ["progress"]        = SolidColorBrush.Parse("#fab387"),
        ["status"]          = SolidColorBrush.Parse("#94e2d5"),
        ["tooltip"]         = SolidColorBrush.Parse("#cba6f7"),
        ["favicon"]         = SolidColorBrush.Parse("#f5c2e7"),
        ["fullscreen"]      = SolidColorBrush.Parse("#fab387"),
        ["ready"]           = SolidColorBrush.Parse("#94e2d5"),
        ["scroll"]          = SolidColorBrush.Parse("#74c7ec"),
        ["autoresize"]      = SolidColorBrush.Parse("#fab387"),
        ["js-dialog"]       = SolidColorBrush.Parse("#cba6f7"),
        ["file-dialog"]     = SolidColorBrush.Parse("#cba6f7"),
        ["contextmenu"]     = SolidColorBrush.Parse("#cba6f7"),
        ["download"]        = SolidColorBrush.Parse("#f5c2e7"),
        ["auth"]            = SolidColorBrush.Parse("#cba6f7"),
        ["find"]            = SolidColorBrush.Parse("#94e2d5"),
        ["renderer"]        = SolidColorBrush.Parse("#f38ba8"),
        ["request"]         = SolidColorBrush.Parse("#74c7ec"),
        ["js-invoke"]       = SolidColorBrush.Parse("#cba6f7"),
        ["a11y"]            = SolidColorBrush.Parse("#94e2d5"),
        ["drag-out"]        = SolidColorBrush.Parse("#fab387"),
        ["permission"]      = SolidColorBrush.Parse("#cba6f7"),
        ["media"]           = SolidColorBrush.Parse("#cba6f7"),
        ["popup"]           = SolidColorBrush.Parse("#f5c2e7"),
        ["cert"]            = SolidColorBrush.Parse("#f38ba8"),
        ["hit-test"]        = SolidColorBrush.Parse("#89dceb"),
        ["capture"]         = SolidColorBrush.Parse("#a6e3a1"),
        ["windowed"]        = SolidColorBrush.Parse("#fab387"),
        ["embedded"]        = SolidColorBrush.Parse("#cba6f7"),
    };

    private static readonly IBrush Fallback = SolidColorBrush.Parse("#a6adc8");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string cat && Map.TryGetValue(cat, out var b) ? b : Fallback;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
