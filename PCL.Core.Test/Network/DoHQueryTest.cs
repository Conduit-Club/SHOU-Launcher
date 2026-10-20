using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ae.Dns.Protocol.Enums;
using Ae.Dns.Protocol.Records;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net.Dns;

namespace PCL.Core.Test.Network;

[TestClass]
public class DoHQueryTest
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task TestIpQuery()
    {
        RequireExternalTests();
        var query = DnsQuery.Instance;
        var addr = await query.QueryForIpAsync("cloudflare.com", TestContext.CancellationTokenSource.Token);
        Assert.IsNotNull(addr);
        Assert.IsGreaterThan(0, addr.Length);
        Console.WriteLine(string.Join(", ", addr.Select(x => x.ToString())));
    }

    [TestMethod]
    public void TestSrvQuery()
    {
        var raw = new byte[]
        {
            0x00, 0x01, // priority
            0x00, 0x0A, // weight
            0x63, 0xDD, // port 25565
            0x04, (byte)'p', (byte)'l', (byte)'a', (byte)'y',
            0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            0x04, (byte)'t', (byte)'e', (byte)'s', (byte)'t',
            0x00
        };
        var srvRecord = new DnsSrvResource();
        var offset = 0;

        srvRecord.ReadBytes(raw, ref offset, raw.Length);

        Assert.AreEqual(raw.Length, offset);
        Assert.AreEqual(1, srvRecord.Priority);
        Assert.AreEqual(10, srvRecord.Weight);
        Assert.AreEqual(25565, srvRecord.Port);
        Assert.AreEqual("play.example.test", srvRecord.Target);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task TestSrvQuery_Integration()
    {
        RequireExternalTests();
        var query = DnsQuery.Instance;
        var addr = await query.QueryAsync("_minecraft._tcp.mc.hdeda6e85.nyat.app", DnsQueryType.SRV, TestContext.CancellationTokenSource.Token);
        Assert.IsNotNull(addr);
        Assert.IsGreaterThan(0, addr.Answers.Count);
        Assert.AreEqual(DnsQueryClass.IN, addr.Header.QueryClass);
        var record = addr.Answers.FirstOrDefault()?.Resource as DnsUnknownResource;
        Assert.IsNotNull(record);
        var srvRecord = new DnsSrvResource();
        var offset = 0;
        srvRecord.ReadBytes(record.Raw, ref offset, record.Raw.Length);
        Console.WriteLine(srvRecord.Target);
        Console.WriteLine(srvRecord.Port);
    }

    private static void RequireExternalTests()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PCL_RUN_EXTERNAL_TESTS"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("External DNS integration test requires PCL_RUN_EXTERNAL_TESTS=1.");
        }
    }

    public TestContext TestContext { get; set; }
}
