using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SHOU.Launcher.Core.Java;

/// <summary>Draft protocol v1 encoder for the trusted Java BootstrapMain, with no filesystem or process access.</summary>
public sealed class JavaBootstrapPayload : IDisposable
{
    public const int ProtocolVersion = 1;
    public const int HeaderBytes = 14;
    public const int MaximumBodyBytes = 1024 * 1024;
    public const int MaximumClassBytes = 4096;
    public const int MaximumArgumentBytes = 256 * 1024;
    public const int MaximumArgumentCount = 65536;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private byte[]? bytes;

    private JavaBootstrapPayload(byte[] value) => bytes = value;
    public int ByteLength => Volatile.Read(ref bytes)?.Length ?? 0;
    [JsonIgnore]
    public ReadOnlyMemory<byte> Bytes => Volatile.Read(ref bytes)
        ?? throw new ObjectDisposedException(nameof(JavaBootstrapPayload));

    public static JavaBootstrapPayload Create(string mainClass, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(mainClass);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaximumArgumentCount) throw new ArgumentException("Bootstrap argument count exceeds the protocol limit.", nameof(arguments));
        using var body = new MemoryStream(MaximumBodyBytes);
        try
        {
            WriteString(body, mainClass, MaximumClassBytes, allowEmpty: false);
            WriteInt32(body, arguments.Count);
            foreach (var argument in arguments)
                WriteString(body, argument, MaximumArgumentBytes, allowEmpty: true);
            var output = new byte[HeaderBytes + checked((int)body.Length)];
            "SHOUBOOT"u8.CopyTo(output);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8, 2), ProtocolVersion);
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(10, 4), checked((int)body.Length));
            body.GetBuffer().AsSpan(0, checked((int)body.Length)).CopyTo(output.AsSpan(HeaderBytes));
            return new JavaBootstrapPayload(output);
        }
        finally { CryptographicOperations.ZeroMemory(body.GetBuffer()); }
    }

    private static void WriteString(MemoryStream target, string value, int maximum, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0) || value.Contains('\0'))
            throw new ArgumentException("Bootstrap UTF-8 field is null, empty or contains NUL.");
        int length;
        try { length = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Bootstrap field contains invalid Unicode."); }
        if (length > maximum || target.Length + 4L + length > MaximumBodyBytes)
            throw new ArgumentException("Bootstrap UTF-8 field exceeds the bounded protocol.");
        WriteInt32(target, length);
        // Encoding directly into the one allocated stream buffer avoids a temporary argument byte array.
        var offset = checked((int)target.Position);
        target.SetLength(offset + length);
        Utf8.GetBytes(value.AsSpan(), target.GetBuffer().AsSpan(offset, length));
        target.Position = offset + length;
    }
    private static void WriteInt32(MemoryStream target, int value)
    {
        if (target.Length + 4 > MaximumBodyBytes) throw new ArgumentException("Bootstrap payload exceeds the protocol limit.");
        Span<byte> encoded = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(encoded, value); target.Write(encoded);
    }
    public void Dispose()
    {
        var value = Interlocked.Exchange(ref bytes, null);
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }
    public override string ToString() => nameof(JavaBootstrapPayload) + " [redacted; " + ByteLength + " bytes]";
}
