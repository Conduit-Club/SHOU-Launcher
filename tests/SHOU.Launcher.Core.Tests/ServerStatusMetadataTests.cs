using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SkiaSharp;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ServerStatusMetadataTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task StatusMetadataComesFromTheSameResponseAndBadIconsDoNotHideOnlineCounts(bool badIcon)
    {
        using var image = new SKBitmap(64, 64); image.Erase(SKColors.Green); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var json = JsonSerializer.Serialize(new { version = new { name = "1.21.1", protocol = 767 }, players = new { online = 2, max = 16 }, description = new { text = "§aFixture", extra = new[] { new { text = " MOTD" } } }, favicon = badIcon ? "data:image/png;base64,invalid" : "data:image/png;base64," + Convert.ToBase64String(png.ToArray()) });
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(); await using var stream = client.GetStream();
                // Drain the handshake and status request before closing. Windows resets a TCP
                // connection closed with unread request bytes, potentially discarding the reply.
                var input = new byte[1]; await stream.ReadExactlyAsync(input);
                var request = new byte[input[0] + 2]; await stream.ReadExactlyAsync(request);
                var text = Encoding.UTF8.GetBytes(json); using var payload = new MemoryStream(); payload.WriteByte(0); VarInt(payload, text.Length); payload.Write(text); using var packet = new MemoryStream(); VarInt(packet, (int)payload.Length); payload.Position = 0; await payload.CopyToAsync(packet); await stream.WriteAsync(packet.ToArray());
            });
            var result = await ClubStatusProbe.ProbeAsync(new("fixture", "Fixture", "127.0.0.1:" + port)); await server;
            Assert.AreEqual("在线", result.Status); Assert.AreEqual(2, result.OnlinePlayers); Assert.AreEqual(16, result.MaximumPlayers); Assert.AreEqual("Fixture MOTD", result.Motd); Assert.AreEqual("1.21.1", result.GameVersion); Assert.IsTrue(result.ResponseMilliseconds >= 0);
            if (badIcon) Assert.IsNull(result.IconPng); else CollectionAssert.AreEqual(png.ToArray(), result.IconPng!);
        }
        finally { listener.Stop(); }
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task ProtocolPingChecksThePongNonceWithoutLosingAValidStatus(bool wrongNonce)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(); await using var stream = client.GetStream(); var length = new byte[1]; await stream.ReadExactlyAsync(length); var request = new byte[length[0] + 2]; await stream.ReadExactlyAsync(request);
                var json = Encoding.UTF8.GetBytes("{\"players\":{\"online\":1},\"description\":\"Ping fixture\"}"); using var payload = new MemoryStream(); payload.WriteByte(0); VarInt(payload, json.Length); payload.Write(json); using var packet = new MemoryStream(); VarInt(packet, (int)payload.Length); payload.Position = 0; await payload.CopyToAsync(packet); await stream.WriteAsync(packet.ToArray());
                var ping = new byte[10]; await stream.ReadExactlyAsync(ping); Assert.AreEqual(9, ping[0]); Assert.AreEqual(1, ping[1]); if (wrongNonce) ping[9] ^= 1; await stream.WriteAsync(ping);
            });
            var result = await ClubStatusProbe.ProbeAsync(new("fixture", "Fixture", "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port)); await server;
            Assert.AreEqual("在线", result.Status); Assert.AreEqual("Ping fixture", result.Motd);
            if (wrongNonce) Assert.IsNull(result.PingMilliseconds); else Assert.IsTrue(result.PingMilliseconds >= 0);
        }
        finally { listener.Stop(); }
    }
    [TestMethod]
    public async Task LateStatusRemainsOnlineWhenTheSharedDeadlineExpiresWaitingForPong()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(); await using var stream = client.GetStream(); var length = new byte[1]; await stream.ReadExactlyAsync(length); var request = new byte[length[0] + 2]; await stream.ReadExactlyAsync(request);
                await Task.Delay(3500); var json = Encoding.UTF8.GetBytes("{\"players\":{\"online\":1},\"description\":\"Late status\"}"); using var payload = new MemoryStream(); payload.WriteByte(0); VarInt(payload, json.Length); payload.Write(json); using var packet = new MemoryStream(); VarInt(packet, (int)payload.Length); payload.Position = 0; await payload.CopyToAsync(packet); await stream.WriteAsync(packet.ToArray());
                var ping = new byte[10]; await stream.ReadExactlyAsync(ping); await Task.Delay(600); // Cross the probe's 4 s deadline without a Pong.
            });
            var result = await ClubStatusProbe.ProbeAsync(new("fixture", "Fixture", "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port)); await server;
            Assert.AreEqual("在线", result.Status); Assert.AreEqual("Late status", result.Motd); Assert.AreEqual(1, result.OnlinePlayers); Assert.IsNull(result.PingMilliseconds);
        }
        finally { listener.Stop(); }
    }
    [TestMethod]
    public async Task ExplicitCancellationIsNotReportedAsAnOfflineServer()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ClubStatusProbe.ProbeAsync(new("fixture", "Fixture", "127.0.0.1:25565"), cancel.Token));
    }
    private static void VarInt(Stream stream, int value)
    { do { var b = value & 0x7f; value >>>= 7; stream.WriteByte((byte)(b | (value != 0 ? 0x80 : 0))); } while (value != 0); }
}
