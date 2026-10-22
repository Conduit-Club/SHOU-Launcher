using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.Minecraft.Java;

/// <summary>Reads JKS v1/v2 without running Java. Retained entries are copied byte-for-byte.</summary>
public sealed class JksTrustStore
{
    public sealed record Entry(string Alias, string? Fingerprint, byte[] Encoded, bool HasDistrustedKeyCertificate = false);
    public IReadOnlyList<Entry> Entries { get; }
    private readonly int _version;
    private JksTrustStore(int version, List<Entry> entries) { _version = version; Entries = entries; }

    public static JksTrustStore Read(byte[] data, string password = "changeit")
    {
        if (data.Length < 32 || data.Length > 32 * 1024 * 1024) throw new InvalidDataException("证书库大小无效。");
        var body = data.AsSpan(0, data.Length - 20);
        if (!CryptographicOperations.FixedTimeEquals(Digest(body, password), data.AsSpan(data.Length - 20)))
            throw new InvalidDataException("JKS 完整性校验失败：密码不是默认 changeit，或文件已损坏。请使用 Java 厂商工具更新。");
        var reader = new Reader(body.ToArray());
        if (reader.Int() != unchecked((int)0xfeedfeed)) throw new InvalidDataException("不是 JKS 证书库。");
        var version = reader.Int();
        if (version is not (1 or 2)) throw new InvalidDataException("不支持的 JKS 版本。");
        var count = reader.Count();
        var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var start = reader.Position;
            var tag = reader.Int();
            var alias = reader.Utf();
            reader.Skip(8); // Java timestamp
            string? fingerprint = null;
            var distrustedKeyCertificate = false;
            if (tag == 2) fingerprint = ReadCert(reader, version);
            else if (tag == 1)
            {
                reader.Skip(reader.Int()); // Encrypted private key, retained unchanged.
                var chainCount = reader.Count();
                for (var j = 0; j < chainCount; j++)
                    distrustedKeyCertificate |= JavaTrustPolicy.Distrusted.Contains(ReadCert(reader, version));
            }
            else throw new InvalidDataException("未知的 JKS 条目类型。");
            entries.Add(new Entry(alias, fingerprint, body.Slice(start, reader.Position - start).ToArray(), distrustedKeyCertificate));
        }
        if (reader.Position != body.Length) throw new InvalidDataException("JKS 包含未解析的数据。");
        return new JksTrustStore(version, entries);
    }

    public byte[] Remove(IReadOnlySet<string> fingerprints, string password = "changeit")
    {
        var retained = Entries.Where(e => e.Fingerprint is null || !fingerprints.Contains(e.Fingerprint)).ToArray();
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(header, unchecked((int)0xfeedfeed));
        BinaryPrimitives.WriteInt32BigEndian(header[4..], _version);
        BinaryPrimitives.WriteInt32BigEndian(header[8..], retained.Length);
        stream.Write(header);
        foreach (var entry in retained) stream.Write(entry.Encoded);
        var body = stream.ToArray();
        stream.Write(Digest(body, password));
        return stream.ToArray();
    }

    private static string ReadCert(Reader reader, int version)
    {
        if (version == 2 && reader.Utf() != "X.509") throw new InvalidDataException("不支持的证书类型。");
        var cert = reader.Bytes(reader.Int());
        // Validate DER before presenting a clean result.
        using var parsed = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(cert);
        return Convert.ToHexString(SHA256.HashData(cert));
    }

    private static byte[] Digest(ReadOnlySpan<byte> body, string password)
    {
        // JKS mandates SHA-1 for file integrity. Certificate matching uses SHA-256.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.BigEndianUnicode.GetBytes(password));
        hash.AppendData("Mighty Aphrodite"u8);
        hash.AppendData(body);
        return hash.GetHashAndReset();
    }

    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }
        public int Int() => BinaryPrimitives.ReadInt32BigEndian(Bytes(4));
        public int Count() { var n = Int(); return n is >= 0 and <= 100000 ? n : throw new InvalidDataException("条目数量无效。"); }
        public string Utf() { var n = BinaryPrimitives.ReadUInt16BigEndian(Bytes(2)); return Encoding.UTF8.GetString(Bytes(n)); }
        public void Skip(int n) => Bytes(n);
        public byte[] Bytes(int n)
        {
            if (n < 0 || n > data.Length - Position) throw new InvalidDataException("JKS 文件被截断或长度无效。");
            var result = data.AsSpan(Position, n).ToArray(); Position += n; return result;
        }
    }
}
