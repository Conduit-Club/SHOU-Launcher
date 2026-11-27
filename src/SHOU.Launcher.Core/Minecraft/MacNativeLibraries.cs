using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Minecraft;

public static class MacNativeLibraries
{
    public static IReadOnlySet<RuntimeArchitecture> Architectures(Stream input)
    {
        Span<byte> header = stackalloc byte[8]; input.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header); var result = new HashSet<RuntimeArchitecture>();
        if (magic is 0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe)
        {
            var cpu = magic is 0xcefaedfe or 0xcffaedfe ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            Add(cpu); return result;
        }
        if (magic is not (0xcafebabe or 0xbebafeca or 0xcafebabf or 0xbfbafeca)) return result;
        var little = magic is 0xbebafeca or 0xbfbafeca;
        var count = little ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (count is 0 or > 32) throw new InvalidDataException("Mac 原生库的 Universal 架构表无效。");
        var entry = new byte[magic is 0xcafebabf or 0xbfbafeca ? 32 : 20];
        for (var i = 0; i < count; i++) { input.ReadExactly(entry); Add(little ? BinaryPrimitives.ReadUInt32LittleEndian(entry) : BinaryPrimitives.ReadUInt32BigEndian(entry)); }
        return result;
        void Add(uint cpu) { var value = cpu switch { 0x0100000c => RuntimeArchitecture.Arm64, 0x01000007 => RuntimeArchitecture.X64, 7 => RuntimeArchitecture.X86, _ => RuntimeArchitecture.Unknown }; if (value != RuntimeArchitecture.Unknown) result.Add(value); }
    }
    public static IReadOnlyList<string> ValidateAndAlias(IReadOnlyList<string> files, RuleContext context)
    {
        if (context.OSName != "osx") return files;
        var result = files.ToList();
        foreach (var path in files)
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not (".dylib" or ".jnilib")) continue;
            SafePath.RejectLinks(path); using (var stream = File.OpenRead(path))
                if (!Architectures(stream).Contains(context.JavaArchitecture)) throw new PlatformNotSupportedException($"Mac 原生库 {Path.GetFileName(path)} 不支持所选 Java 架构 {context.JavaArchitecture}；未启动游戏。");
            if (!path.EndsWith(".jnilib", StringComparison.OrdinalIgnoreCase)) continue;
            var alias = Path.ChangeExtension(path, ".dylib"); SafePath.RejectLinks(alias);
            if (File.Exists(alias))
            {
                using var first = File.OpenRead(path); using var second = File.OpenRead(alias);
                if (!SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second))) throw new InvalidDataException("Mac 原生库别名与原文件不一致。");
            }
            else File.Copy(path, alias);
            result.Add(alias);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }
    public static void ValidateMcMouser(string gameDirectory, RuleContext context)
    {
        if (context.OSName != "osx") return;
        var mods = Path.Combine(gameDirectory, "mods"); if (!Directory.Exists(mods)) return; SafePath.RejectLinks(mods);
        // Inspect the targeted native override only. Other mod JARs may legitimately contain binaries for many systems.
        foreach (var path in Directory.EnumerateFiles(mods, "*.jar").Where(p => Path.GetFileName(p).Contains("mcmouser", StringComparison.OrdinalIgnoreCase)).Take(16))
        {
            SafePath.RejectLinks(path); using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry("mcmouser/natives/liblwjgl.dylib") is not { } entry) continue;
            using var input = entry.Open();
            if (!Architectures(input).Contains(context.JavaArchitecture))
                throw new PlatformNotSupportedException($"McMouser 自带原生库不支持 Java {context.JavaArchitecture}；请使用匹配的 Mac 版本，未修改模组或切换架构。");
        }
    }
}
