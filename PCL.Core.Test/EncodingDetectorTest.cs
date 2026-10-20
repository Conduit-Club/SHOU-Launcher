using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Codecs;

namespace PCL.Core.Test;
[TestClass]
public class EncodingDetectorTest
{
    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        // Code pages are opt-in on .NET; the production startup path registers this
        // provider, while the isolated test host does not run that startup service.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [TestMethod]
    public void TestEncoding()
    {
        var utf8 = Encoding.UTF8.GetBytes("Hi, There!");
        Assert.AreEqual(EncodingDetector.DetectEncoding(utf8), Encoding.UTF8);
        utf8 = Encoding.UTF8.GetBytes("棍斤拷烫烫烫");
        Assert.AreEqual(EncodingDetector.DetectEncoding(utf8), Encoding.UTF8);
        var gb2312 = Encoding.GetEncoding("gb2312");
        var gb = gb2312.GetBytes("你好世界");
        var detectedGb = EncodingDetector.DetectEncoding(gb);
        Assert.AreEqual(Encoding.Default.CodePage, detectedGb.CodePage,
            "无 BOM 的非 UTF-8 数据应回退到系统默认编码");
        Assert.AreEqual("你好世界", EncodingUtils.DecodeBytes(gb));
        // var gbnew = Encoding.GetEncoding("GB18030").GetBytes("你好世界");
        // Assert.AreEqual(EncodingDetector.DetectEncoding(gbnew), Encoding.GetEncoding("GB18030"));
        byte[] nonEncode = [0xfe, 0x5f, 0xa1];
        Assert.AreEqual(Encoding.Default.CodePage, EncodingDetector.DetectEncoding(nonEncode).CodePage);
    }

    [TestMethod]
    public void DetectEncoding_ReadFromBeginUsesZeroAndRestoresPosition()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("€"))
            .ToArray();
        using var stream = new MemoryStream(bytes);
        stream.Position = 2;

        var detected = EncodingDetector.DetectEncoding(stream, readFromBegin: true);

        Assert.AreEqual(Encoding.Unicode.CodePage, detected.CodePage);
        Assert.AreEqual(2, stream.Position);
    }

    [TestMethod]
    public void DetectEncoding_AllowsUtf8SequenceAcrossSampleBoundary()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 1023) + "你");
        using var stream = new MemoryStream(bytes);

        var detected = EncodingDetector.DetectEncoding(stream);

        Assert.AreEqual(Encoding.UTF8.CodePage, detected.CodePage);
    }
}
