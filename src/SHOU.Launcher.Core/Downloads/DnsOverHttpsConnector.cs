using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SHOU.Launcher.Core.Downloads;

public static class DnsOverHttpsConnector
{
    private static readonly HttpClient Dns = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(5) };
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, HttpClient? client = null, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        if (!host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)) return await System.Net.Dns.GetHostAddressesAsync(host, ct);
        using var response = await (client ?? Dns).GetAsync("https://dns.google/resolve?name=" + Uri.EscapeDataString(host) + "&type=A", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("DNS response too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var data = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (data.Length + count > 65536) throw new InvalidDataException("DNS response too large."); data.Write(buffer, 0, count); }
        using var json = JsonDocument.Parse(data.ToArray()); List<IPAddress> addresses = [];
        if (json.RootElement.TryGetProperty("Answer", out var answers))
            foreach (var answer in answers.EnumerateArray().Take(32))
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() is 1 or 28 && IPAddress.TryParse(answer.GetProperty("data").GetString(), out var address)) addresses.Add(address);
        if (addresses.Count == 0) throw new IOException("DNS response did not include an address.");
        return addresses;
    }
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> addresses;
        try { addresses = await ResolveAsync(context.DnsEndPoint.Host, ct: ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct); }
        Exception? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, true); }
            catch (Exception ex) when (ex is SocketException or IOException) { failure = ex; socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new IOException("No DNS address could be connected.", failure);
    }
}
