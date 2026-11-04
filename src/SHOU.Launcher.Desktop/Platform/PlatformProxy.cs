using System.Net;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform;

public static class PlatformProxy
{
    /// <summary>Creates a per-client handler; never changes system settings or HttpClient.DefaultProxy.</summary>
    public static HttpClientHandler CreateHandler(ProxySettings settings, NetworkCredential? credentials = null)
    {
        if (settings.Mode == ProxyMode.Direct) return new HttpClientHandler { UseProxy = false };
        // .NET 9+ refreshes Windows registry proxy changes; macOS uses the native/default proxy implementation.
        // https://devblogs.microsoft.com/dotnet/dotnet-9-networking-improvements/#proxy-auto-update-on-windows
        if (settings.Mode == ProxyMode.System) return new HttpClientHandler { UseProxy = true, Proxy = HttpClient.DefaultProxy };
        if (settings.Mode != ProxyMode.Custom || !Uri.TryCreate(settings.Address, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")
            || !string.IsNullOrEmpty(address.UserInfo))
            throw new ArgumentException("A custom proxy requires a valid address; credentials must be supplied by the credential store.");
        return new HttpClientHandler { UseProxy = true, Proxy = new WebProxy(address) { Credentials = credentials, BypassProxyOnLocal = false } };
    }
}
