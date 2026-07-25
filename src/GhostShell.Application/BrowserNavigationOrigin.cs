using System.Globalization;

namespace GhostShell.Application;

/// <summary>
/// A canonical top-level browser origin used by the native renderer to
/// contain one governed navigation and every redirect it starts.
/// </summary>
public sealed record BrowserNavigationOrigin
{
    private BrowserNavigationOrigin(
        string scheme,
        string idnHost,
        int port,
        bool isBlank)
    {
        Scheme = scheme;
        IdnHost = idnHost;
        Port = port;
        IsBlank = isBlank;
    }

    public string Scheme { get; }

    public string IdnHost { get; }

    public int Port { get; }

    public bool IsBlank { get; }

    /// <summary>
    /// Stable presentation and digest material for this exact effective
    /// origin. HTTP(S) ports are always explicit.
    /// </summary>
    public string CanonicalValue =>
        IsBlank
            ? BrowserAddress.Blank.ToString()
            : string.Concat(
                Scheme,
                "://",
                CanonicalHost(IdnHost),
                ":",
                Port.ToString(CultureInfo.InvariantCulture));

    public static BrowserNavigationOrigin FromAddress(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var value = address.Value;
        if (value.AbsoluteUri.Equals(
                BrowserAddress.Blank.Value.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserNavigationOrigin(
                "about",
                string.Empty,
                port: -1,
                isBlank: true);
        }

        return new BrowserNavigationOrigin(
            value.Scheme.ToLowerInvariant(),
            value.IdnHost.ToLowerInvariant(),
            value.Port,
            isBlank: false);
    }

    public bool Allows(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var candidate = FromAddress(address);
        return IsBlank
            ? candidate.IsBlank
            : !candidate.IsBlank
              && string.Equals(
                  Scheme,
                  candidate.Scheme,
                  StringComparison.Ordinal)
              && string.Equals(
                  IdnHost,
                  candidate.IdnHost,
                  StringComparison.Ordinal)
              && Port == candidate.Port;
    }

    public override string ToString() => CanonicalValue;

    private static string CanonicalHost(string host) =>
        host.Contains(':', StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
