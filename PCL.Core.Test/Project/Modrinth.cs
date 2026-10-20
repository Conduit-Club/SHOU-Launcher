using System;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace PCL.Core.Test.Project;

[TestClass]
public class Modrinth
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task GetProjectTest()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PCL_RUN_EXTERNAL_TESTS"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("External Modrinth test requires PCL_RUN_EXTERNAL_TESTS=1.");
        }

        using var c = new HttpClient();
        using var req = new HttpRequestMessage();
        req.RequestUri = new Uri("https://api.modrinth.com/v2/project/sodium");
        req.Method = HttpMethod.Get;
        var ret = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.IsNotNull(ret);
        ret.EnsureSuccessStatusCode();
        var s = await ret!.Content.ReadAsStreamAsync();
        Assert.IsNotNull(s);
        // var instance = (ModrinthProject)(await JsonSerializer.DeserializeAsync(
        //     s,
        //     typeof(ModrinthProject),
        //     JsonSerializerOptions.Web) ?? throw new NullReferenceException());
        // Assert.AreEqual("sodium", instance.slug);
    }
}
