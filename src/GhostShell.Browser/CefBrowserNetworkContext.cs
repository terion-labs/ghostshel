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

    private CefBrowserNetworkContext(CefRequestContext context)
    {
        _context = context;
    }

    public static CefBrowserNetworkContext Create(int socksProxyPort)
    {
        var preferences = RequiredPreferences(socksProxyPort);
        var context = Cef.CreateRequestContext()
            ?? throw new InvalidOperationException(
                "The embedded browser could not create an isolated network context.");
        try
        {
            foreach (var preference in preferences)
            {
                SetRequiredPreferenceJson(context, preference.Key, preference.Value);
            }

            return new CefBrowserNetworkContext(context);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    public CefBrowserView CreateView() => new(_context);

    public void Dispose() => _context.Dispose();

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

        var proxy = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = "fixed_servers",
            ["server"] = $"socks5://127.0.0.1:{socksProxyPort}",
            ["bypass_list"] = "<-loopback>",
        };
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proxy"] = JsonSerializer.Serialize(proxy),
            ["webrtc.ip_handling_policy"] = JsonSerializer.Serialize(
                "disable_non_proxied_udp"),
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
