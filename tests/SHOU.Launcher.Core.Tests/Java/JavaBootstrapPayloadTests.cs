using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Java;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
public sealed class JavaBootstrapPayloadTests
{
    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public void GoldenFrameUsesVersionedBigEndianLengthsAndPreservesEmptyArgument()
    {
        using var payload = JavaBootstrapPayload.Create("Echo", ["x", ""]);
        byte[] expected = [83, 72, 79, 85, 66, 79, 79, 84, 0, 1, 0, 0, 0, 21,
            0, 0, 0, 4, 69, 99, 104, 111, 0, 0, 0, 2, 0, 0, 0, 1, 120, 0, 0, 0, 0];
        CollectionAssert.AreEqual(expected, payload.Bytes.ToArray());
    }

    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public void ExplicitUtf8CarriesUnicodeAndWhitespaceWithoutShellOrArgfileEscaping()
    {
        const string argument = "中文 😀 \" \\ \n\r\t# @ $(synthetic); & |";
        using var payload = JavaBootstrapPayload.Create("Echo", [argument]);
        var frame = payload.Bytes.Span;
        Assert.AreEqual(JavaBootstrapPayload.ProtocolVersion, BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(8, 2)));
        Assert.AreEqual(frame.Length - JavaBootstrapPayload.HeaderBytes, BinaryPrimitives.ReadInt32BigEndian(frame.Slice(10, 4)));
        var argumentLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(26, 4));
        Assert.AreEqual(Encoding.UTF8.GetByteCount(argument), argumentLength);
        Assert.AreEqual(argument, Encoding.UTF8.GetString(frame.Slice(30, argumentLength)));
    }

    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public void InvalidUnicodeNulAndBoundsFailWithoutEchoingInput()
    {
        Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create("", []));
        Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create("Echo", [null!]));
        var invalid = Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create("Echo", ["synthetic\ud800only"]));
        Assert.IsFalse(invalid.ToString().Contains("synthetic", StringComparison.Ordinal));
        Assert.IsNull(invalid.InnerException);
        Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create("Echo", ["synthetic\0only"]));
        Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create(new string('x', JavaBootstrapPayload.MaximumClassBytes + 1), []));
        Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create("Echo", [new string('x', JavaBootstrapPayload.MaximumArgumentBytes + 1)]));
        Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create("Echo", new string[JavaBootstrapPayload.MaximumArgumentCount + 1]));
        Assert.Throws<ArgumentException>(() => JavaBootstrapPayload.Create("Echo", Enumerable.Repeat(new string('x', JavaBootstrapPayload.MaximumArgumentBytes), 4).ToArray()));
    }

    [TestMethod]
    [TestCategory("JavaArgumentProtection")]
    public void ReportsOmitBytesAndDisposalClearsTheOwnedFrame()
    {
        var payload = JavaBootstrapPayload.Create("Echo", ["synthetic-token-never-real"]);
        var borrowed = payload.Bytes;
        Assert.IsFalse(payload.ToString().Contains("synthetic-token", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(payload)))
        {
            Assert.AreEqual(1, document.RootElement.EnumerateObject().Count());
            Assert.AreEqual(borrowed.Length, document.RootElement.GetProperty("ByteLength").GetInt32());
        }
        payload.Dispose(); payload.Dispose();
        CollectionAssert.AreEqual(new byte[borrowed.Length], borrowed.ToArray());
        Assert.AreEqual(0, payload.ByteLength);
        Assert.Throws<ObjectDisposedException>(() => _ = payload.Bytes);
    }
}
