using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public static class ClubStatusProbe
{
    public static async Task<ClubServer> ProbeAsync(ClubServer server, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var uri = new Uri("minecraft://" + server.Address); var port = uri.IsDefaultPort || uri.Port < 0 ? 25565 : uri.Port;
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
            var json = JsonNode.Parse(bytes); return server with { Status = "在线", OnlinePlayers = (int?)json?["players"]?["online"] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Text.Json.JsonException or UriFormatException)
        { return server with { Status = "状态暂不可用", OnlinePlayers = null }; }
    }
    private static void WriteVarInt(Stream stream, int value) { do { var b = value & 0x7f; value >>>= 7; stream.WriteByte((byte)(b | (value != 0 ? 0x80 : 0))); } while (value != 0); }
    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    { var value = 0; var bytes = new byte[1]; for (var index = 0; index < 5; index++) { await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); value |= (bytes[0] & 0x7f) << (index * 7); if ((bytes[0] & 0x80) == 0) return value; } throw new InvalidDataException("Oversized VarInt."); }
}
