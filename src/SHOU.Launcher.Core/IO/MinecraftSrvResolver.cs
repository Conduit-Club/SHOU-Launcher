using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SHOU.Launcher.Core.IO;

/// <summary>Small bounded SRV lookup against configured system DNS; preserves the original forced-host name.</summary>
public static class MinecraftSrvResolver
{
    public static async Task<int> ResolvePortAsync(string host, CancellationToken ct)
    {
        var queryName = "_minecraft._tcp." + host.TrimEnd('.');
        var id = (ushort)RandomNumberGenerator.GetInt32(65536);
        using var packet = new MemoryStream();
        var header = new byte[12]; BinaryPrimitives.WriteUInt16BigEndian(header, id); header[2] = 1; header[5] = 1; packet.Write(header);
        foreach (var part in queryName.Split('.'))
        { var bytes = Encoding.ASCII.GetBytes(part); if (bytes.Length is 0 or > 63) return 25565; packet.WriteByte((byte)bytes.Length); packet.Write(bytes); }
        packet.Write([0, 0, 33, 0, 1]);
        var resolvers = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().DnsAddresses).Distinct().Take(3).ToArray();
        foreach (var resolver in resolvers)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(700);
            try
            {
                using var client = new UdpClient(resolver.AddressFamily); client.Connect(resolver, 53);
                await client.SendAsync(packet.ToArray(), timeout.Token).ConfigureAwait(false);
                var response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (ParsePort(response.Buffer, id, queryName) is { } port) return port;
                return 25565; // A valid negative reply is authoritative for this query.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or OperationCanceledException) { }
        }
        return 25565;
    }
    public static int? ParsePort(byte[] data, ushort id, string question)
    {
        int U16(int offset) => offset < 0 || offset + 2 > data.Length ? throw new InvalidDataException("Truncated DNS record.") : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
        string Name(ref int position)
        {
            var cursor = position; var jumped = false; var labels = new List<string>(); var steps = 0;
            while (true)
            {
                if (cursor >= data.Length || ++steps > 128) throw new InvalidDataException("Invalid DNS name.");
                var length = data[cursor++];
                if ((length & 0xc0) == 0xc0)
                {
                    if (cursor >= data.Length) throw new InvalidDataException("Truncated DNS pointer.");
                    if (!jumped) position = cursor + 1;
                    cursor = ((length & 0x3f) << 8) | data[cursor]; jumped = true; continue;
                }
                if (length == 0) { if (!jumped) position = cursor; return string.Join('.', labels); }
                if (length > 63 || cursor + length > data.Length) throw new InvalidDataException("Invalid DNS label.");
                labels.Add(Encoding.ASCII.GetString(data, cursor, length)); cursor += length;
            }
        }
        if (data.Length < 12 || U16(0) != id || (U16(2) & 0x820f) != 0x8000 || U16(4) != 1) throw new InvalidDataException("Invalid DNS response.");
        var offset = 12;
        if (!Name(ref offset).Equals(question, StringComparison.OrdinalIgnoreCase) || U16(offset) != 33 || U16(offset + 2) != 1) throw new InvalidDataException("DNS question mismatch.");
        offset += 4; var answers = U16(6); if (answers > 128) throw new InvalidDataException("Too many DNS answers.");
        var ports = new List<(int Priority, int Port)>();
        for (var i = 0; i < answers; i++)
        {
            var name = Name(ref offset); var type = U16(offset); var recordClass = U16(offset + 2); var length = U16(offset + 8); offset += 10;
            if (offset + length > data.Length) throw new InvalidDataException("Truncated DNS answer.");
            if (name.Equals(question, StringComparison.OrdinalIgnoreCase) && type == 33 && recordClass == 1 && length >= 7)
            {
                var priority = U16(offset); var port = U16(offset + 4); var targetOffset = offset + 6; var target = Name(ref targetOffset);
                if (target.Length > 0 && port > 0 && targetOffset <= offset + length) ports.Add((priority, port));
            }
            offset += length;
        }
        return ports.Count == 0 ? null : ports.OrderBy(p => p.Priority).First().Port;
    }
}
