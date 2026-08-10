using System.Text.Json;

namespace GhostShell.Browser.Tests;

public sealed class CefBrowserNetworkContextTests
{
    [Fact]
    public void Ssh_proxy_routes_loopback_and_blocks_non_proxied_webrtc_udp()
    {
        var preferences = CefBrowserNetworkContext.RequiredPreferences(45001);
        using var proxyDocument = JsonDocument.Parse(preferences["proxy"]);
        var proxy = proxyDocument.RootElement;

        Assert.Equal("fixed_servers", proxy.GetProperty("mode").GetString());
        Assert.Equal(
            "socks5://127.0.0.1:45001",
            proxy.GetProperty("server").GetString());
        Assert.Equal("<-loopback>", proxy.GetProperty("bypass_list").GetString());
        Assert.Equal(
            "disable_non_proxied_udp",
            JsonSerializer.Deserialize<string>(
                preferences["webrtc.ip_handling_policy"]));
        Assert.DoesNotContain("proxy.mode", preferences.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void Proxy_port_must_be_a_tcp_port(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CefBrowserNetworkContext.RequiredPreferences(port));
    }
}
