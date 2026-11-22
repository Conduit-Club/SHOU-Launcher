using System.Buffers.Binary;
using System.IO.Compression;

namespace SHOU.Launcher.Core.Instances;

public enum NbtType : byte { End, Byte, Short, Int, Long, Float, Double, ByteArray, String, List, Compound, IntArray, LongArray }
public enum NbtCompression { Plain, Gzip }
public sealed record NbtLimits(int MaxInputBytes = 8 * 1024 * 1024, int MaxExpandedBytes = 32 * 1024 * 1024,
    int MaxDepth = 64, int MaxNodes = 250000, int MaxArrayElements = 4 * 1024 * 1024, int MaxStringBytes = 65535);
public sealed record NbtList(NbtType ElementType, IReadOnlyList<NbtTag> Items);
// Float and Double payloads retain IEEE bits (uint/ulong), including otherwise lossy NaN payloads.
public sealed record NbtTag(NbtType Type, string Name, object Value)
{
    public IReadOnlyList<NbtTag> Children => Type == NbtType.Compound ? (IReadOnlyList<NbtTag>)Value : throw new InvalidDataException("NBT compound required.");
    public NbtTag? Find(string name)
    {
        var found = Children.Where(t => t.Name == name).Take(2).ToArray();
        if (found.Length > 1) throw new InvalidDataException("Ambiguous duplicate NBT field: " + name);
        return found.SingleOrDefault();
    }
}
public sealed record NbtDocument(NbtTag Root, NbtCompression Compression = NbtCompression.Plain);

/// <summary>Java edition named, big-endian NBT. Deliberately rejects unknown types, negative lengths and trailing data.</summary>
public static class BoundedNbt
{
    public static NbtDocument Read(ReadOnlyMemory<byte> input, NbtLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var budget = limits ?? new(); ValidateLimits(budget);
        if (input.Length > budget.MaxInputBytes) throw new InvalidDataException("NBT input exceeds its byte budget.");
        var compression = input.Length >= 2 && input.Span[0] == 0x1f && input.Span[1] == 0x8b ? NbtCompression.Gzip : NbtCompression.Plain;
        var bytes = compression == NbtCompression.Gzip ? Expand(input, budget, cancellationToken) : input.ToArray();
        if (bytes.Length > budget.MaxExpandedBytes) throw new InvalidDataException("NBT expanded byte budget exceeded.");
        var reader = new Reader(bytes, budget, cancellationToken);
        var root = reader.Named(0);
        if (root.Type != NbtType.Compound) throw new InvalidDataException("NBT file root must be a named compound.");
        if (!reader.AtEnd) throw new InvalidDataException("Trailing bytes after NBT root are unsupported.");
        return new(root, compression);
    }

    public static byte[] Write(NbtDocument document, NbtLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var budget = limits ?? new(); ValidateLimits(budget);
        if (document.Root.Type != NbtType.Compound) throw new InvalidDataException("NBT file root must be a named compound.");
        using var stream = new MemoryStream(); var writer = new Writer(stream, budget, cancellationToken);
        writer.Named(document.Root, 0); var plain = stream.ToArray();
        byte[] output;
        if (document.Compression == NbtCompression.Plain) output = plain;
        else if (document.Compression == NbtCompression.Gzip)
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true)) gzip.Write(plain);
            output = compressed.ToArray();
        }
        else throw new InvalidDataException("Unsupported NBT compression.");
        if (output.Length > budget.MaxInputBytes) throw new InvalidDataException("Encoded NBT exceeds its file byte budget.");
        return output;
    }

    private static void ValidateLimits(NbtLimits limits)
    {
        if (limits.MaxInputBytes <= 0 || limits.MaxExpandedBytes <= 0 || limits.MaxDepth is < 1 or > 256
            || limits.MaxNodes <= 0 || limits.MaxArrayElements <= 0 || limits.MaxStringBytes is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }
    private static byte[] Expand(ReadOnlyMemory<byte> input, NbtLimits limits, CancellationToken ct)
    {
        if (input.Length < 18 || input.Span[2] != 8 || (input.Span[3] & 0xe0) != 0) throw new InvalidDataException("Unsupported gzip NBT header.");
        using var source = new MemoryStream(input.ToArray(), false); using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream(); var buffer = new byte[65536];
        while (true)
        {
            ct.ThrowIfCancellationRequested(); var read = gzip.Read(buffer);
            if (read == 0) break;
            if (output.Length + read > limits.MaxExpandedBytes) throw new InvalidDataException("NBT gzip expansion exceeds its byte budget.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray(); var footer = input.Span[^8..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != (uint)bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(footer) != Crc32(bytes))
            throw new InvalidDataException("Invalid or unsupported multi-member gzip NBT trailer.");
        return bytes;
    }
    internal static uint Crc32(ReadOnlySpan<byte> bytes, uint previous = 0)
    {
        var crc = ~previous;
        foreach (var b in bytes) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        return ~crc;
    }

    private sealed class Reader(byte[] bytes, NbtLimits limits, CancellationToken ct)
    {
        private int offset, nodes;
        public bool AtEnd => offset == bytes.Length;
        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > bytes.Length - offset) throw new InvalidDataException("Truncated NBT payload.");
            var result = bytes.AsSpan(offset, length); offset += length; return result;
        }
        private byte Byte() => Take(1)[0];
        private int Int() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
        private string Text()
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
            if (count > limits.MaxStringBytes) throw new InvalidDataException("NBT string byte budget exceeded.");
            var data = Take(count); var chars = new char[count]; var used = 0;
            // DataInput/DataOutput modified UTF-8 encodes UTF-16 code units, including surrogate pairs.
            for (var i = 0; i < data.Length;)
            {
                var first = data[i++]; int value;
                if (first <= 0x7f) value = first;
                else if ((first & 0xe0) == 0xc0)
                {
                    if (i >= data.Length || (data[i] & 0xc0) != 0x80) throw new InvalidDataException("Invalid modified UTF-8 continuation.");
                    value = ((first & 0x1f) << 6) | (data[i++] & 0x3f);
                    if (value is > 0 and < 0x80 || value == 0 && first != 0xc0) throw new InvalidDataException("Noncanonical modified UTF-8 sequence.");
                }
                else if ((first & 0xf0) == 0xe0)
                {
                    if (i + 1 >= data.Length || (data[i] & 0xc0) != 0x80 || (data[i + 1] & 0xc0) != 0x80) throw new InvalidDataException("Invalid modified UTF-8 continuation.");
                    value = ((first & 15) << 12) | ((data[i++] & 63) << 6) | (data[i++] & 63);
                    if (value < 0x800) throw new InvalidDataException("Noncanonical modified UTF-8 sequence.");
                }
                else throw new InvalidDataException("Java modified UTF-8 permits only one, two and three byte code units.");
                chars[used++] = (char)value;
            }
            return new string(chars, 0, used);
        }
        public NbtTag Named(int depth)
        {
            var type = (NbtType)Byte();
            if (type is <= NbtType.End or > NbtType.LongArray) throw new InvalidDataException("Unsupported named NBT tag type.");
            return Payload(type, Text(), depth);
        }
        private int Count(int width)
        {
            var count = Int();
            if (count < 0 || count > limits.MaxArrayElements || (long)count * width > bytes.Length - offset)
                throw new InvalidDataException("Invalid or over-budget NBT array length.");
            return count;
        }
        private NbtTag Payload(NbtType type, string name, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > limits.MaxDepth || ++nodes > limits.MaxNodes) throw new InvalidDataException("NBT depth or node budget exceeded.");
            object value;
            switch (type)
            {
                case NbtType.Byte: value = unchecked((sbyte)Byte()); break;
                case NbtType.Short: value = BinaryPrimitives.ReadInt16BigEndian(Take(2)); break;
                case NbtType.Int: value = Int(); break;
                case NbtType.Long: value = BinaryPrimitives.ReadInt64BigEndian(Take(8)); break;
                case NbtType.Float: value = BinaryPrimitives.ReadUInt32BigEndian(Take(4)); break;
                case NbtType.Double: value = BinaryPrimitives.ReadUInt64BigEndian(Take(8)); break;
                case NbtType.ByteArray: value = Take(Count(1)).ToArray(); break;
                case NbtType.String: value = Text(); break;
                case NbtType.IntArray:
                    var ints = new int[Count(4)]; for (var i = 0; i < ints.Length; i++) ints[i] = Int(); value = ints; break;
                case NbtType.LongArray:
                    var longs = new long[Count(8)]; for (var i = 0; i < longs.Length; i++) longs[i] = BinaryPrimitives.ReadInt64BigEndian(Take(8)); value = longs; break;
                case NbtType.List:
                    var elementType = (NbtType)Byte(); var length = Count(0);
                    if (elementType > NbtType.LongArray || elementType == NbtType.End && length != 0 || length > limits.MaxNodes - nodes)
                        throw new InvalidDataException("Invalid or over-budget NBT list.");
                    var items = new NbtTag[length]; for (var i = 0; i < length; i++) items[i] = Payload(elementType, "", depth + 1);
                    value = new NbtList(elementType, items); break;
                case NbtType.Compound:
                    var children = new List<NbtTag>();
                    while (true)
                    {
                        if (offset >= bytes.Length) throw new InvalidDataException("Unterminated NBT compound.");
                        if (bytes[offset] == 0) { offset++; break; }
                        children.Add(Named(depth + 1));
                    }
                    value = children; break;
                default: throw new InvalidDataException("Unsupported NBT tag type.");
            }
            return new(type, name, value);
        }
    }

    private sealed class Writer(MemoryStream output, NbtLimits limits, CancellationToken ct)
    {
        private int nodes;
        private void Put(ReadOnlySpan<byte> data)
        {
            if (output.Length + data.Length > limits.MaxExpandedBytes) throw new InvalidDataException("NBT output byte budget exceeded.");
            output.Write(data);
        }
        private void Byte(byte value) { Span<byte> data = stackalloc byte[1]; data[0] = value; Put(data); }
        private void Int(int value) { Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(data, value); Put(data); }
        private void Long(long value) { Span<byte> data = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(data, value); Put(data); }
        private void Text(string value)
        {
            var count = 0;
            foreach (var ch in value) { count += ch is >= '\u0001' and <= '\u007f' ? 1 : ch <= 0x7ff ? 2 : 3; if (count > limits.MaxStringBytes) throw new InvalidDataException("NBT string byte budget exceeded."); }
            Span<byte> prefix = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)count); Put(prefix);
            foreach (var ch in value)
                if (ch is >= '\u0001' and <= '\u007f') Byte((byte)ch);
                else if (ch <= 0x7ff) { Byte((byte)(0xc0 | (ch >> 6))); Byte((byte)(0x80 | (ch & 63))); }
                else { Byte((byte)(0xe0 | (ch >> 12))); Byte((byte)(0x80 | ((ch >> 6) & 63))); Byte((byte)(0x80 | (ch & 63))); }
        }
        public void Named(NbtTag tag, int depth)
        {
            if (tag.Type is <= NbtType.End or > NbtType.LongArray) throw new InvalidDataException("Unsupported named NBT type.");
            Byte((byte)tag.Type); Text(tag.Name); Payload(tag, depth);
        }
        private void Count(int length) { if (length > limits.MaxArrayElements) throw new InvalidDataException("NBT array budget exceeded."); Int(length); }
        private void Payload(NbtTag tag, int depth)
        {
            ct.ThrowIfCancellationRequested(); if (depth > limits.MaxDepth || ++nodes > limits.MaxNodes) throw new InvalidDataException("NBT depth or node budget exceeded.");
            try
            {
                switch (tag.Type)
                {
                    case NbtType.Byte: Byte(unchecked((byte)(sbyte)tag.Value)); break;
                    case NbtType.Short: Span<byte> shortBytes = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(shortBytes, (short)tag.Value); Put(shortBytes); break;
                    case NbtType.Int: Int((int)tag.Value); break;
                    case NbtType.Long: Long((long)tag.Value); break;
                    case NbtType.Float: Int(unchecked((int)(uint)tag.Value)); break;
                    case NbtType.Double: Long(unchecked((long)(ulong)tag.Value)); break;
                    case NbtType.ByteArray: var bytes = (byte[])tag.Value; Count(bytes.Length); Put(bytes); break;
                    case NbtType.String: Text((string)tag.Value); break;
                    case NbtType.IntArray: var ints = (int[])tag.Value; Count(ints.Length); foreach (var item in ints) Int(item); break;
                    case NbtType.LongArray: var longs = (long[])tag.Value; Count(longs.Length); foreach (var item in longs) Long(item); break;
                    case NbtType.List:
                        var list = (NbtList)tag.Value;
                        if (list.ElementType > NbtType.LongArray || list.ElementType == NbtType.End && list.Items.Count != 0 || list.Items.Count > limits.MaxNodes - nodes)
                            throw new InvalidDataException("Invalid or over-budget NBT list.");
                        Byte((byte)list.ElementType); Count(list.Items.Count);
                        foreach (var item in list.Items) { if (item.Type != list.ElementType) throw new InvalidDataException("NBT list contains mixed types."); Payload(item, depth + 1); }
                        break;
                    case NbtType.Compound: foreach (var child in tag.Children) Named(child, depth + 1); Byte(0); break;
                    default: throw new InvalidDataException("Unsupported NBT tag type.");
                }
            }
            catch (InvalidCastException ex) { throw new InvalidDataException("NBT payload does not match its tag type.", ex); }
        }
    }
}
