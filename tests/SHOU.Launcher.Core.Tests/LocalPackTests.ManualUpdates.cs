using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LocalPackTests
{
    private sealed class ManualUpdateBuild : IBuildConfiguration
    {
        public string? MicrosoftClientId => null;
        public string? CurseForgeApiKey => FixtureKey;
        public string SourceSha => "fixture";
        public string Configuration => "Release";
        public string OwnerAssembly => "fixture";
    }
    [TestMethod]
    [DataRow("mod", false)]
    [DataRow("mod", true)]
    [DataRow("resourcepack", false)]
    [DataRow("shader", false)]
    public async Task MissingCfUrlStillAppearsAsUpdateAndVerifiedManualFilesResumeBackupAndReplace(string kind, bool dependency)
    {
        var previous = Mod(); var next = previous with { FileId = 1002, Name = kind == "mod" ? "new.jar" : "new.zip", Bytes = [1, 2, 3, 4, 5] };
        if (kind != "mod") previous = previous with { Name = "original.zip" };
        var child = previous with { Project = 102, FileId = 1003, Name = "dependency.jar", Bytes = [8, 9, 10] };
        var downloads = 0;
        using var handler = new FixtureApi(previous, next, child);
        JsonObject FileNode(Asset file) => new() { ["id"] = file.FileId, ["modId"] = file.Project, ["gameId"] = 432, ["displayName"] = file.Name, ["fileName"] = file.Name,
            ["fileLength"] = file.Bytes.Length, ["fileDate"] = file.FileId == previous.FileId ? "2026-01-01T00:00:00Z" : "2026-01-02T00:00:00Z", ["releaseType"] = 1,
            ["downloadUrl"] = null, ["isAvailable"] = true, ["fileStatus"] = 4, ["gameVersions"] = new JsonArray("1.21.1", "Fabric"),
            ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = file.Sha1 }),
            ["dependencies"] = dependency && file.FileId == next.FileId ? new JsonArray(new JsonObject { ["modId"] = child.Project, ["relationType"] = 3 }) : new JsonArray() };
        handler.Override = req =>
        {
            if (req.RequestUri!.Host != "api.curseforge.com") { downloads++; throw new AssertFailedException("Manual file must never fall back to a guessed download URL."); }
            var path = req.RequestUri.AbsolutePath; JsonNode data;
            if (path.EndsWith("/files/" + previous.FileId, StringComparison.Ordinal)) data = FileNode(previous);
            else if (path.EndsWith("/files/" + next.FileId, StringComparison.Ordinal)) data = FileNode(next);
            else if (path.EndsWith("/files/" + child.FileId, StringComparison.Ordinal)) data = FileNode(child);
            else if (path.EndsWith("/101/files", StringComparison.Ordinal)) data = new JsonArray(FileNode(next), FileNode(previous));
            else if (path.EndsWith("/102/files", StringComparison.Ordinal)) data = new JsonArray(FileNode(child));
            else
            {
                var id = path.EndsWith("/102", StringComparison.Ordinal) ? 102 : 101;
                data = new JsonObject { ["id"] = id, ["gameId"] = 432, ["classId"] = id == 102 || kind == "mod" ? 6 : kind == "shader" ? 6552 : 12,
                    ["slug"] = "fixture-" + id, ["name"] = "Fixture", ["links"] = new JsonObject { ["websiteUrl"] = "https://www.curseforge.com/projects/" + id } };
            }
            return ScriptedHttpHandler.Json(new JsonObject { ["data"] = data }.ToJsonString());
        };
        using var http = new HttpClient(handler, false); var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        var game = Path.Combine(platform.Paths.InstancesDirectory, "fixture"); var relative = (kind == "mod" ? "mods/" : kind == "shader" ? "shaderpacks/" : "resourcepacks/") + previous.Name;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(game, relative))!); await File.WriteAllBytesAsync(Path.Combine(game, relative), previous.Bytes);
        var instance = new GameInstance("fixture", "Fixture", "fabric-loader-0.16.14-1.21.1", game, Loader: "fabric", LoaderVersion: "0.16.14", Installed: true, GameVersionId: "1.21.1");
        var manifest = new ContentInstallManifest(1, instance.Id, [new("cf-101", "cf-101-1001", kind, relative, previous.Sha1, previous.Sha512, previous.Bytes.Length)]);
        await File.WriteAllBytesAsync(Path.Combine(game, ContentInstallFiles.ManifestName), ContentInstallFiles.SerializeManifest(manifest));
        await using (var config = new ConfigurationStore(platform.Paths)) await config.SaveAsync(new(new(), [instance], []));
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), new ManualUpdateBuild(), contentTransportFactory: () => new HttpClient(handler, false));
        var scan = kind == "mod" ? await service.CheckInstanceModUpdatesAsync(instance.Id) : await service.CheckInstanceResourceUpdatesAsync(instance.Id, kind == "shader" ? "shaders" : "resourcepacks");
        Assert.HasCount(1, scan.Updates, string.Join("\n", scan.Notices)); var token = scan.Updates[0].Token;
        var missing = await service.GetInstanceUpdateManualFilesAsync(token); Assert.HasCount(dependency ? 2 : 1, missing);
        var source = Path.Combine(TestFiles.Root(), "picked.bin"); await File.WriteAllTextAsync(source, "wrong");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ProvideInstanceUpdateManualFileAsync(token, missing[0], source));
        CollectionAssert.AreEqual(previous.Bytes, await File.ReadAllBytesAsync(Path.Combine(game, relative)));
        foreach (var file in missing)
        {
            await File.WriteAllBytesAsync(source, file.FileId == child.FileId ? child.Bytes : next.Bytes);
            await service.ProvideInstanceUpdateManualFileAsync(token, file, source);
        }
        Assert.HasCount(0, await service.GetInstanceUpdateManualFilesAsync(token));
        var result = kind == "mod" ? await service.ApplyInstanceModUpdateAsync(token) : await service.ApplyInstanceResourceUpdateAsync(token);
        Assert.IsTrue(Directory.Exists(result.BackupDirectory));
        var updated = kind == "mod" ? "mods/" + next.Name : relative;
        CollectionAssert.AreEqual(next.Bytes, await File.ReadAllBytesAsync(Path.Combine(game, updated)));
        if (dependency) CollectionAssert.AreEqual(child.Bytes, await File.ReadAllBytesAsync(Path.Combine(game, "mods/" + child.Name)));
        Assert.AreEqual(0, downloads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetInstanceUpdateManualFilesAsync(token));
    }
}
