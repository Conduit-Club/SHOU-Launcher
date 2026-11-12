using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ContentMetadataTests
{
    [TestMethod]
    [DataRow("resourcepacks", "resourcepack", false, false, 1)]
    [DataRow("shaders", "shader", false, false, 1)]
    [DataRow("resourcepacks", "mod", false, false, 0)]
    [DataRow("resourcepacks", "resourcepack", true, false, 0)]
    [DataRow("shaders", "shader", false, true, 0)]
    public async Task OnlyExactFileIdentityAndProjectKindCanEnrichDisplayAndValidResultsAreCached(string section, string projectKind, bool wrongSize, bool wrongId, int expected)
    {
        var bytes = "copy-only resource display fixture"u8.ToArray(); var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)); var sha512 = Convert.ToHexStringLower(SHA512.HashData(bytes)); var calls = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            calls++; var path = request.RequestUri!.AbsolutePath; object body;
            if (path.Contains("/version_file/")) body = new { id = "version", project_id = "project", name = "File version", version_number = "2.1", version_type = "release", date_published = "2026-11-01T00:00:00Z", game_versions = new[] { "1.21.1" }, loaders = new[] { "minecraft" }, dependencies = Array.Empty<object>(), files = new[] { new { filename = "display.zip", url = "https://cdn.modrinth.com/data/project/versions/version/display.zip", size = bytes.Length + (wrongSize ? 1 : 0), primary = true, hashes = new { sha1, sha512 } } } };
            else if (path.EndsWith("/project/project")) body = new { id = wrongId ? "different" : "project", slug = "fixture", title = "Online pack title", description = "Description", project_type = projectKind, downloads = 1, categories = new[] { "vanilla-like" } };
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(ScriptedHttpHandler.Json(JsonSerializer.Serialize(body)));
        }));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); var game = Path.Combine(platform.Paths.InstancesDirectory, "fixture"); var folder = Path.Combine(game, section == "shaders" ? "shaderpacks" : section); Directory.CreateDirectory(folder); var file = Path.Combine(folder, "display.zip"); await File.WriteAllBytesAsync(file, bytes);
        var instance = new GameInstance("fixture", "Fixture", "1.21.1", game, GameVersionId: "1.21.1"); await using (var store = new ConfigurationStore(platform.Paths)) await store.SaveAsync(new(new(), [instance], []));
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions()); await service.InitializeAsync();
        var result = await service.ReadInstanceContentMetadataAsync(instance.Id, section); Assert.HasCount(expected, result.Info); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file));
        if (expected == 1)
        {
            Assert.AreEqual("Online pack title", result.Info.Single().Project.Title); Assert.AreEqual("2.1", result.Info.Single().Version); var before = calls;
            var again = await service.ReadInstanceContentMetadataAsync(instance.Id, section); Assert.HasCount(1, again.Info); Assert.AreEqual(before, calls);
        }
        else Assert.IsNotEmpty(result.Notices);
    }
}
