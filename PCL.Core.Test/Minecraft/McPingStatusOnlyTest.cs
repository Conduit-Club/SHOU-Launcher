using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.McPing;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class McPingStatusOnlyTest
{
    [TestMethod]
    public async Task StatusOnlyDoesNotSendPingBeforeStatusResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var serverTask = _RunStrictStatusServerAsync(listener, timeout.Token);

        using var ping = McPingServiceFactory.CreateServiceByHost("127.0.0.1", endpoint.Port, timeout: 1000);
        var result = await ping.PingStatusOnlyAsync(timeout.Token);
        var observed = await serverTask;

        Assert.IsTrue(observed.HandshakeReceived);
        Assert.AreEqual("127.0.0.1", observed.HandshakeHost);
        Assert.IsTrue(observed.StatusRequestReceived);
        Assert.IsFalse(observed.PingArrivedBeforeStatus);
        Assert.IsNotNull(result);
        Assert.AreEqual("strict-test", result!.Version.Name);
        Assert.AreEqual(2, result.Players.Online);
        Assert.AreEqual(40, result.Players.Max);
    }

    private static async Task<StrictServerObservation> _RunStrictStatusServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var handshake = await _ReadPacketAsync(stream, cancellationToken);
        var statusRequest = await _ReadPacketAsync(stream, cancellationToken);

        // A strict proxy may wait for the status response and then close without reading a
        // client ping.  Give an early ping a chance to arrive before replying.
        await Task.Delay(100, cancellationToken);
        var pingArrived = stream.DataAvailable;

        var statusJson = Encoding.UTF8.GetBytes(
            "{\"version\":{\"name\":\"strict-test\",\"protocol\":772}," +
            "\"players\":{\"online\":2,\"max\":40},\"description\":\"strict\"}");
        var payload = _Join(
            _EncodeVarInt(0),
            _EncodeVarInt(statusJson.Length),
            statusJson);
        await _WritePacketAsync(stream, payload, cancellationToken);

        return new StrictServerObservation(
            handshake.Length > 0 && handshake[0] == 0,
            _ReadHandshakeHost(handshake),
            statusRequest.Length > 0 && statusRequest[0] == 0,
            pingArrived);
    }

    private static string _ReadHandshakeHost(byte[] packet)
    {
        using var stream = new MemoryStream(packet, writable: false);
        _ReadVarInt(stream); // packet id
        _ReadVarInt(stream); // protocol version
        var length = checked((int)_ReadVarInt(stream));
        var host = new byte[length];
        stream.ReadExactly(host);
        return Encoding.UTF8.GetString(host);
    }

    private static async Task<byte[]> _ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = checked((int)await _ReadVarIntAsync(stream, cancellationToken));
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task<long> _ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        long value = 0;
        var shift = 0;
        while (true)
        {
            var oneByte = new byte[1];
            await stream.ReadExactlyAsync(oneByte, cancellationToken);
            value |= (long)(oneByte[0] & 0x7F) << shift;
            if ((oneByte[0] & 0x80) == 0) return value;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("VarInt is too long.");
        }
    }

    private static long _ReadVarInt(Stream stream)
    {
        long value = 0;
        var shift = 0;
        while (true)
        {
            var oneByte = stream.ReadByte();
            if (oneByte < 0) throw new EndOfStreamException();
            value |= (long)(oneByte & 0x7F) << shift;
            if ((oneByte & 0x80) == 0) return value;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("VarInt is too long.");
        }
    }

    private static async Task _WritePacketAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var packet = _Join(_EncodeVarInt(payload.Length), payload);
        await stream.WriteAsync(packet, cancellationToken);
    }

    private static byte[] _EncodeVarInt(int value)
    {
        var bytes = new List<byte>();
        var unsigned = (uint)value;
        do
        {
            var oneByte = (byte)(unsigned & 0x7F);
            unsigned >>= 7;
            if (unsigned != 0) oneByte |= 0x80;
            bytes.Add(oneByte);
        } while (unsigned != 0);

        return bytes.ToArray();
    }

    private static byte[] _Join(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts) bytes.AddRange(part);
        return bytes.ToArray();
    }

    private sealed record StrictServerObservation(
        bool HandshakeReceived,
        string HandshakeHost,
        bool StatusRequestReceived,
        bool PingArrivedBeforeStatus);
}
