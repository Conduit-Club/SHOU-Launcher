using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Buffers.Binary;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public static class ClubStatusProbe
{
    public static async Task<ClubServer> ProbeAsync(ClubServer server, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var elapsed = Stopwatch.StartNew();
        try
        {
            var uri = new Uri("minecraft://" + server.Address);
            var port = uri.Port >= 0 ? uri.Port : await IO.MinecraftSrvResolver.ResolvePortAsync(uri.DnsSafeHost, timeout.Token).ConfigureAwait(false);
            using var tcp = new TcpClient(); await tcp.ConnectAsync(uri.Host, port, timeout.Token).ConfigureAwait(false);
            await using var stream = tcp.GetStream();
            using var payload = new MemoryStream(); WriteVarInt(payload, 0); WriteVarInt(payload, 767);
            var host = Encoding.UTF8.GetBytes(uri.Host); WriteVarInt(payload, host.Length); payload.Write(host); payload.WriteByte((byte)(port >> 8)); payload.WriteByte((byte)port); WriteVarInt(payload, 1);
            using var packet = new MemoryStream(); WriteVarInt(packet, checked((int)payload.Length)); payload.Position = 0; payload.CopyTo(packet); packet.Write([1, 0]);
            await stream.WriteAsync(packet.ToArray(), timeout.Token).ConfigureAwait(false);
            var length = await ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false); if (length is < 2 or > 1048576) throw new InvalidDataException("Invalid server status length.");
            if (await ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false) != 0) throw new InvalidDataException("Invalid server status packet.");
            var jsonLength = await ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false); if (jsonLength is < 2 or > 1048576 || jsonLength > length) throw new InvalidDataException("Invalid server status JSON length.");
            var bytes = new byte[jsonLength]; await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            var json = JsonNode.Parse(bytes); var online = (int?)json?["players"]?["online"];
            if (online is null or < 0) throw new InvalidDataException("Invalid online player count.");
            var version = json?["version"]?["name"] is JsonValue versionValue && versionValue.TryGetValue<string>(out var versionText) ? versionText : null;
            int? maximum = json?["players"]?["max"] is JsonValue maxValue && maxValue.TryGetValue<int>(out var max) && max >= 0 ? max : null;
            var favicon = json?["favicon"] is JsonValue iconValue && iconValue.TryGetValue<string>(out var iconText) ? iconText : null;
            var queryMilliseconds = elapsed.ElapsedMilliseconds;
            var ping = await PingAsync(stream, timeout.Token).ConfigureAwait(false);
            return server with { Status = "在线", OnlinePlayers = online, MaximumPlayers = maximum, Motd = PlainMotd(json?["description"]), GameVersion = version is null ? null : version[..Math.Min(128, version.Length)], ResponseMilliseconds = queryMilliseconds, PingMilliseconds = ping, IconPng = Icon(favicon) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException or System.Text.Json.JsonException or UriFormatException or InvalidOperationException or FormatException)
        { return server with { Status = "状态暂不可用", OnlinePlayers = null, MaximumPlayers = null, Motd = null, PingMilliseconds = null, ResponseMilliseconds = null, GameVersion = null, IconPng = null }; }
    }
    private static async Task<long?> PingAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
        try
        {
            var nonce = Stopwatch.GetTimestamp(); var packet = new byte[10]; packet[0] = 9; packet[1] = 1; BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(2), nonce);
            var elapsed = Stopwatch.StartNew(); await stream.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
            if (await ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false) != 9 || await ReadVarIntAsync(stream, timeout.Token).ConfigureAwait(false) != 1) return null;
            var reply = new byte[8]; await stream.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            return BinaryPrimitives.ReadInt64BigEndian(reply) == nonce ? elapsed.ElapsedMilliseconds : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { return null; }
    }
    private static string PlainMotd(JsonNode? value)
    {
        var result = new StringBuilder(); var remaining = 128;
        void Append(JsonNode? node, int depth)
        {
            if (node is null || depth > 16 || --remaining < 0 || result.Length >= 4096) return;
            if (node is JsonValue literal && literal.TryGetValue<string>(out var text)) result.Append(text[..Math.Min(text.Length, 4096 - result.Length)]);
            else if (node is JsonArray array) { foreach (var item in array) { if (remaining <= 0 || result.Length >= 4096) break; Append(item, depth + 1); } }
            else if (node is JsonObject obj) { Append(obj["text"], depth + 1); Append(obj["extra"], depth + 1); }
        }
        Append(value, 0); return System.Text.RegularExpressions.Regex.Replace(result.ToString(), "§[0-9A-FK-ORa-fk-or]", "");
    }
    private static byte[]? Icon(string? value)
    {
        const string prefix = "data:image/png;base64,";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length > 48 * 1024) return null;
        try
        {
            var bytes = Convert.FromBase64String(value[prefix.Length..]);
            return bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) && BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) == 64 && BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) == 64 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }
    private static void WriteVarInt(Stream stream, int value) { do { var b = value & 0x7f; value >>>= 7; stream.WriteByte((byte)(b | (value != 0 ? 0x80 : 0))); } while (value != 0); }
    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    { var value = 0; var bytes = new byte[1]; for (var index = 0; index < 5; index++) { await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); value |= (bytes[0] & 0x7f) << (index * 7); if ((bytes[0] & 0x80) == 0) return value; } throw new InvalidDataException("Oversized VarInt."); }
}
