using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class RemainingPageParityTests
{
    [TestMethod]
    public void WpfCardsImagesAndLinksParseWithoutExecutingXaml()
    {
        var nodes = CustomHomeDocument.Parse("<local:MyCard Title=\"Welcome\"><StackPanel><TextBlock Text=\"Body\"/><Image Source=\"banner.png\"/><local:MyButton Text=\"Wiki\" EventType=\"打开网页\" EventData=\"https://example.com/\"/></StackPanel></local:MyCard>");
        Assert.AreEqual("Card", nodes.Single().Kind); Assert.AreEqual("Welcome", nodes.Single().Text);
        var children = nodes.Single().Children.Single().Children; Assert.AreEqual("banner.png", children[1].Source); Assert.AreEqual("https://example.com/", children[2].Link);
        Assert.AreEqual("plain text", CustomHomeDocument.Parse("plain text").Single().Text);
        Assert.Throws<InvalidDataException>(() => CustomHomeDocument.Parse("<local:MyButton EventType=\"ExecuteCommand\" EventData=\"touch marker\"/>"));
        Assert.Throws<System.Xml.XmlException>(() => CustomHomeDocument.Parse("<!DOCTYPE Root [<!ENTITY f SYSTEM 'file:///etc/passwd'>]><TextBlock>&f;</TextBlock>"));
        Assert.Throws<InvalidDataException>(() => CustomHomeDocument.Parse("<Button Click=\"Handler\" EventType=\"OpenUrl\" EventData=\"https://example.com\"/>"));
        Assert.Throws<InvalidDataException>(() => CustomHomeDocument.Parse("<local:MyButton EventType=\"打开网页\" EventData=\"file:///tmp/f\"/>"));
    }
    [TestMethod]
    public void PortableHomeParsesGridDefinitionsAndMultipleOldEventsWithoutLoadingHandlers()
    {
        var node = CustomHomeDocument.Parse("\uFEFF<?xml version=\"1.0\"?><Grid><Grid.ColumnDefinitions><ColumnDefinition Width=\"2*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions><local:MyButton Grid.Column=\"1\"><local:MyButton.Events><local:CustomEvent Type=\"复制文本\" Data=\"hello\"/><local:CustomEvent Type=\"弹出提示\" Data=\"done|Finish\"/></local:MyButton.Events></local:MyButton></Grid>").Single();
        Assert.AreEqual("Grid", node.Kind); Assert.AreEqual("2*,Auto", node.Properties!["ColumnDefinitions"]); var button = node.Children.Single(); Assert.AreEqual("1", button.Properties!["Grid.Column"]); CollectionAssert.AreEqual(new[] { "CopyText", "ShowHint" }, button.Actions!.Select(a => a.Type).ToArray());
        Assert.Throws<InvalidDataException>(() => CustomHomeDocument.Parse("<Button EventType=\"ShowDialog\" EventData=\"missing separator\"/>"));
        Assert.Throws<InvalidDataException>(() => CustomHomeDocument.Parse("<Button EventType=\"OpenUrl\" EventData=\"https://user:secret@example.com/\"/>"));
    }
    [TestMethod]
    public void WikiReferencesUseProviderSpecificSlugsAndOnlySupportedResourceTypes()
    {
        var project = new ContentProject("AANobbMI", "sodium", "Sodium", "Description", "Author", 1, "mod", null, new("https://modrinth.com/mod/sodium"));
        var reference = ContentReferenceIndex.Find(project); Assert.IsNotNull(reference); Assert.IsTrue(reference.Id > 0); Assert.AreEqual($"https://www.mcmod.cn/class/{reference.Id}.html", reference.PageUri.AbsoluteUri);
        Assert.IsNull(ContentReferenceIndex.Find(project with { Kind = "resourcepack" })); Assert.IsNull(ContentReferenceIndex.Find(project with { Slug = "unknown-fixture-never-a-real-mod" }));
    }
    [TestMethod]
    public async Task DescriptionTranslationIsAnonymousExplicitCachedAndHandlesMissingEntries()
    {
        var calls = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            calls++; Assert.AreEqual("mod.mcimirror.top", request.RequestUri!.Host); Assert.IsNull(request.Headers.Authorization); Assert.IsFalse(request.Headers.Contains("x-api-key"));
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/missing") ? new HttpResponseMessage(HttpStatusCode.NotFound) : ScriptedHttpHandler.Json("{\"translated\":\"中文介绍\"}"));
        }));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), publicInformationHttp: http);
        var project = new ContentProject("cf-42", "fixture", "Title", "Original", "Author", 1, "mod", null, new("https://www.curseforge.com/minecraft/mc-mods/fixture"), Source: "curseforge");
        Assert.AreEqual("中文介绍", await service.GetChineseContentDescriptionAsync(project)); Assert.AreEqual("中文介绍", await service.GetChineseContentDescriptionAsync(project)); Assert.AreEqual(1, calls);
        Assert.IsNull(await service.GetChineseContentDescriptionAsync(project with { Id = "missing", Source = "modrinth" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetChineseContentDescriptionAsync(project with { Id = "../private" }));
    }
    [TestMethod]
    public async Task HomeImagesResolveRelativeSourcesAndRemoteHomesCannotReadLocalFiles()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root); var home = Path.Combine(root, "Custom.xaml"); await File.WriteAllTextAsync(home, "<TextBlock Text=\"Fixture\"/>"); var image = Path.Combine(root, "banner.png"); await File.WriteAllBytesAsync(image, [1, 2, 3]);
        using var http = new HttpClient(new ScriptedHttpHandler(request => { Assert.AreEqual("https://example.com/home/banner.png", request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5]) }); }));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), publicInformationHttp: http);
        Assert.AreEqual("<TextBlock Text=\"Fixture\"/>", await service.ReadHomeDocumentAsync(home, false)); CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await service.ReadCustomHomeImageAsync("banner.png", home, false));
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, await service.ReadCustomHomeImageAsync("banner.png", "https://example.com/home/Custom.xaml", true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadCustomHomeImageAsync("file:///etc/passwd", "https://example.com/home/Custom.xaml", true));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadHomeDocumentAsync(home, false, cancel.Token));
    }
}
