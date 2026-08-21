using System.Text.Json;
using Exclr8Cef;

namespace GhostShell.Browser;

/// <summary>
/// Owns one isolated CEF request context configured for an SSH-backed SOCKS
/// route. Chromium normally bypasses proxies for loopback names; removing that
/// implicit rule is what makes http://localhost refer to the SSH host.
/// </summary>
internal sealed class CefBrowserNetworkContext : IDisposable
{
    private readonly CefRequestContext _context;
    private readonly CefBrowserContentPolicy _contentPolicy;

    private CefBrowserNetworkContext(
        CefRequestContext context,
        CefBrowserContentPolicy contentPolicy)
    {
        _context = context;
        _contentPolicy = contentPolicy;
    }

    public static CefBrowserNetworkContext CreateIsolatedHtmlPreview()
    {
        return CreateConfigured(
            HtmlPreviewPreferences(),
            CefBrowserContentPolicy.RestrictedLocalPreview,
            "The embedded browser could not create a restricted HTML-preview context.");
    }

    public static CefBrowserNetworkContext Create(int socksProxyPort)
    {
        return CreateConfigured(
            RequiredPreferences(socksProxyPort),
            CefBrowserContentPolicy.Ordinary,
            "The embedded browser could not create an isolated network context.");
    }

    public CefBrowserView CreateView() => new(_context, _contentPolicy);

    public void Dispose() => _context.Dispose();

    internal static IReadOnlyDictionary<string, string> HtmlPreviewPreferences() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Context-local content settings keep JavaScript disabled across
            // replacement renderers without changing ordinary browser panels.
            ["profile.default_content_setting_values.javascript"] = "2",
            ["profile.default_content_setting_values.popups"] = "2",
        };

    private static CefBrowserNetworkContext CreateConfigured(
        IReadOnlyDictionary<string, string> preferences,
        CefBrowserContentPolicy contentPolicy,
        string creationFailure)
    {
        var context = Cef.CreateRequestContext()
            ?? throw new InvalidOperationException(creationFailure);
        try
        {
            foreach (var preference in preferences)
            {
                SetRequiredPreferenceJson(context, preference.Key, preference.Value);
            }

            return new CefBrowserNetworkContext(context, contentPolicy);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the JSON values accepted by CefRequestContext.SetPreference.
    /// Chromium registers proxy configuration as one dictionary-valued
    /// preference named "proxy"; proxy.mode and similar dotted names are not
    /// independently registered preferences.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RequiredPreferences(
        int socksProxyPort)
    {
        if (socksProxyPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(socksProxyPort),
                "A SOCKS proxy port must be between 1 and 65535.");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proxy"] = $$"""{"mode":"fixed_servers","server":"socks5://127.0.0.1:{{socksProxyPort}}","bypass_list":"<-loopback>"}""",
            ["webrtc.ip_handling_policy"] = "\"disable_non_proxied_udp\"",
        };
    }

    private static void SetRequiredPreferenceJson(
        CefRequestContext context,
        string name,
        string valueJson)
    {
        if (!context.SetPreference(name, valueJson))
        {
            throw new InvalidOperationException(
                $"The embedded browser rejected the required '{name}' network setting.");
        }
    }
}
