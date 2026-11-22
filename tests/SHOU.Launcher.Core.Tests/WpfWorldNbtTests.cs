using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WpfWorldNbtTests
{
    // Independently specified wire fixture: all twelve standard payloads, BE primitives and Java UTF-16 code units.
    internal static byte[] Golden => Convert.FromHexString(string.Concat("""
        0A0000
        01000162FF
        02000173FED4
        0300016912345678
        0400016C0102030405060708
        050001667FC01234
        060001647FF8000000001234
        0700016100000003007FFF
        08000175000C41C080E6BDAEEDA0BDEDB880
        09000171030000000200000001FFFFFFFF
        0A0001630800017600017800
        0B0001780000000210203040FFFFFFFF
        0C000179000000020102030405060708FFFFFFFFFFFFFFFF
        00
        """.Where(c => !char.IsWhiteSpace(c))));

    [TestMethod]
    public void AllStandardTagsAndModifiedUtf8RetainTheirExactWireValues()
    {
        var document = BoundedNbt.Read(Golden);
        Assert.AreEqual(12, document.Root.Children.Count);
        Assert.AreEqual(-1, (sbyte)document.Root.Find("b")!.Value);
        Assert.AreEqual((short)-300, document.Root.Find("s")!.Value);
        Assert.AreEqual(0x12345678, document.Root.Find("i")!.Value);
        Assert.AreEqual(0x0102030405060708L, document.Root.Find("l")!.Value);
        Assert.AreEqual(0x7fc01234u, document.Root.Find("f")!.Value);
        Assert.AreEqual(0x7ff8000000001234ul, document.Root.Find("d")!.Value);
        Assert.AreEqual("A\0潮😀", document.Root.Find("u")!.Value);
        CollectionAssert.AreEqual(Golden, BoundedNbt.Write(document));
        CollectionAssert.AreEqual(new[] { 0x10203040, -1 }, (int[])document.Root.Find("x")!.Value);
        CollectionAssert.AreEqual(new[] { 0x0102030405060708L, -1L }, (long[])document.Root.Find("y")!.Value);
    }

    [TestMethod]
    public void GzipValidatesTrailerAndEnforcesExpansionAndParseBudgets()
    {
        var compressed = Gzip(Golden);
        var decoded = BoundedNbt.Read(compressed);
        Assert.AreEqual(NbtCompression.Gzip, decoded.Compression);
        CollectionAssert.AreEqual(Golden, BoundedNbt.Write(decoded with { Compression = NbtCompression.Plain }));
        var bad = compressed.ToArray(); bad[^8] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(bad));
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(compressed[..^4]));
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(Gzip(new byte[1024 * 1024]), new(MaxExpandedBytes: 4096)));
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(Golden, new(MaxNodes: 3)));
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(Golden, new(MaxArrayElements: 1)));
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(Golden.Concat(new byte[] { 0 }).ToArray()));
    }

    [TestMethod]
    public void UnsupportedTagLengthsDepthAndFourByteUtf8AreRejectedBeforeAllocation()
    {
        foreach (var hex in new[] { "0A000007000161FFFFFFFF00", "0A00000D00016100", "0A000009000161010FFFFFFF00", "0A0000080001610004F09F988000" })
            Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(Convert.FromHexString(hex)));
        var nested = Enumerable.Repeat(new byte[] { 10, 0, 0 }, 8).SelectMany(b => b).Concat(new byte[8]).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbt.Read(nested, new(MaxDepth: 3)));
    }

    [TestMethod, TestCategory("WorldJavaInterop")]
    public async Task JdkDataInputStreamIndependentlyReadsEveryWrittenPayloadAndModifiedUtf8()
    {
        var root = TestFiles.Root(); var fixture = Path.Combine(root, "all-tags.nbt");
        await File.WriteAllBytesAsync(fixture, BoundedNbt.Write(BoundedNbt.Read(Golden)));
        var source = Path.Combine(root, "NbtOracle.java");
        await File.WriteAllTextAsync(source, """
            import java.io.*;
            public class NbtOracle {
              static void check(boolean value) { if (!value) throw new AssertionError("NBT wire mismatch"); }
              static void tag(DataInputStream in, int type, String name) throws Exception { check(in.readUnsignedByte()==type); check(in.readUTF().equals(name)); }
              public static void main(String[] args) throws Exception {
                try (DataInputStream in = new DataInputStream(new FileInputStream(args[0]))) {
                  tag(in,10,""); tag(in,1,"b"); check(in.readByte()==-1);
                  tag(in,2,"s"); check(in.readShort()==-300);
                  tag(in,3,"i"); check(in.readInt()==0x12345678);
                  tag(in,4,"l"); check(in.readLong()==0x0102030405060708L);
                  tag(in,5,"f"); check(Float.floatToRawIntBits(in.readFloat())==0x7fc01234);
                  tag(in,6,"d"); check(Double.doubleToRawLongBits(in.readDouble())==0x7ff8000000001234L);
                  tag(in,7,"a"); check(in.readInt()==3); check(in.readByte()==0); check(in.readByte()==127); check(in.readByte()==-1);
                  tag(in,8,"u"); check(in.readUTF().equals("A\0潮😀"));
                  tag(in,9,"q"); check(in.readByte()==3); check(in.readInt()==2); check(in.readInt()==1); check(in.readInt()==-1);
                  tag(in,10,"c"); tag(in,8,"v"); check(in.readUTF().equals("x")); check(in.readByte()==0);
                  tag(in,11,"x"); check(in.readInt()==2); check(in.readInt()==0x10203040); check(in.readInt()==-1);
                  tag(in,12,"y"); check(in.readInt()==2); check(in.readLong()==0x0102030405060708L); check(in.readLong()==-1);
                  check(in.readByte()==0); check(in.read()==-1); System.out.println("JDK_NBT_ORACLE_PASS");
                }
              }
            }
            """);
        var java = Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? throw new InvalidOperationException("Controlled JDK required."), "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        await using var runner = new ProcessRunner();
        var result = await runner.RunAsync(new ProcessSpec(java, [source, fixture], root, Timeout: TimeSpan.FromSeconds(30)));
        Assert.AreEqual(0, result.ExitCode, result.StandardError); StringAssert.Contains(result.StandardOutput, "JDK_NBT_ORACLE_PASS");
    }
    internal static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(bytes); return output.ToArray();
    }
}
