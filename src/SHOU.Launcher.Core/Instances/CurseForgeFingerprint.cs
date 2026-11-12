namespace SHOU.Launcher.Core.Instances;

/// <summary>CurseForge's whitespace-filtered Murmur2, computed in two bounded-memory passes.</summary>
internal static class CurseForgeFingerprint
{
    public static async Task<uint> ComputeAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[131072]; long length = 0; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) for (var i = 0; i < count; i++) if (Include(buffer[i])) length++;
        if (length > uint.MaxValue) throw new InvalidDataException("模组过大，无法计算 CurseForge 指纹。");
        stream.Position = 0; var hash = 1u ^ (uint)length; uint word = 0; var shift = 0;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            for (var i = 0; i < count; i++)
            {
                if (!Include(buffer[i])) continue;
                word |= (uint)buffer[i] << shift; shift += 8;
                if (shift != 32) continue;
                word *= 0x5bd1e995; word ^= word >> 24; word *= 0x5bd1e995; hash *= 0x5bd1e995; hash ^= word; word = 0; shift = 0;
            }
        if (shift > 0) { hash ^= word; hash *= 0x5bd1e995; }
        hash ^= hash >> 13; hash *= 0x5bd1e995; hash ^= hash >> 15; return hash;
    }
    private static bool Include(byte value) => value is not (9 or 10 or 13 or 32);
}
