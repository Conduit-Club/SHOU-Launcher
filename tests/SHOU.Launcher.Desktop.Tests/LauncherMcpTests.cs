using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Mcp;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class LauncherMcpTests
{
    [AvaloniaFact]
    public async Task ToolsPageControlsAnAuthenticatedStreamableHttpServer()
    {
        using var upstream = new HttpClient(new ResourceProvider());
        var downloader = new VerifiedDownloader(upstream);
        var platform = TestPlatformServices.Create(new(FixturePaths.Create("mcp")), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var launcher = new LauncherService(platform, upstream, downloader, new(), deferAccountRestore: true);
        await launcher.InitializeAsync();
        var instance = await launcher.CreateInstanceAsync("MCP fixture", "1.21.1");
        await using var mcp = new LauncherMcpServer(launcher);
        using var vm = new MainWindowViewModel(launcher, new FakeExternalLauncher(), null, mcp);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            vm.ShowToolsCommand.Execute(null);
            vm.Tools.ShowMcpCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var page = Assert.IsType<McpPageViewModel>(vm.Tools.Mcp);
            Assert.True(Find<ScrollViewer>(window, "McpPage").IsEffectivelyVisible);
            Assert.Equal("已暂停", page.Status);
            Assert.True(Find<Button>(window, "StartMcpButton").IsEffectivelyEnabled);
            Assert.False(Find<Button>(window, "CopyMcpTokenButton").IsEffectivelyEnabled);
            var copyAddress = Find<Button>(window, "CopyMcpAddressButton");
            Assert.True(copyAddress.IsEffectivelyEnabled);
            copyAddress.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var clipboard = window.Clipboard;
            Assert.NotNull(clipboard);
            Assert.Equal(page.Endpoint, await clipboard.TryGetTextAsync());
            await clipboard.ClearAsync();

            using var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            page.Port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            await page.StartCommand.ExecuteAsync(null);
            Assert.False(page.IsRunning);
            Assert.True(page.HasFeedback);
            occupied.Stop();

            await page.StartCommand.ExecuteAsync(null);
            Assert.True(page.IsRunning, page.Feedback);
            Assert.Equal(page.Port, mcp.ReadPort());
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<Button>(window, "PauseMcpButton").IsEffectivelyEnabled);
            Assert.True(Find<Button>(window, "CopyMcpTokenButton").IsEffectivelyEnabled);

            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            const string initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"fixture","version":"1"}}}""";
            using var unauthenticated = await SendAsync(client, page.Endpoint, initialize);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            using var badToken = await SendAsync(client, page.Endpoint, initialize, new string('x', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, badToken.StatusCode);
            using var browser = await SendAsync(client, page.Endpoint, initialize, mcp.Token, origin: "https://example.org");
            Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);
            using var rebound = await SendAsync(client, page.Endpoint, initialize, mcp.Token, host: "example.org");
            Assert.Equal(HttpStatusCode.Forbidden, rebound.StatusCode);

            using var initialized = await RpcAsync(client, page.Endpoint, mcp.Token, initialize);
            Assert.Equal("shou-launcher", initialized.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            using var listed = await RpcAsync(client, page.Endpoint, mcp.Token, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            var names = listed.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Contains("list_instances", names);
            Assert.Contains("install_minecraft_version", names);
            Assert.Contains("download_resource", names);
            Assert.Contains("discover_java", names);
            Assert.Contains("analyze_latest_crash", names);

            var rename = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 3, method = "tools/call",
                @params = new { name = "rename_instance", arguments = new { instanceId = instance.Id, name = "MCP renamed" } }
            });
            using var renamed = await RpcAsync(client, page.Endpoint, mcp.Token, rename);
            Assert.Equal("MCP renamed", launcher.Snapshot.Instances.Single().Name);
            Assert.Contains("MCP renamed", renamed.RootElement.GetProperty("result").GetProperty("content").GetRawText());

            const string skill = """{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"shou://launcher/skill"}}""";
            using var resource = await RpcAsync(client, page.Endpoint, mcp.Token, skill);
            Assert.Equal(LauncherMcpSkill.Read(), resource.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());

            const string download = """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"download_resource","arguments":{"projectId":"fixture-project","releaseId":"fixture-release","fileName":"fixture-pack.zip"}}}""";
            using var downloaded = await RpcAsync(client, page.Endpoint, mcp.Token, download);
            using var downloadResult = ToolOutput(downloaded);
            var downloadedPath = downloadResult.RootElement.GetProperty("path").GetString()!;
            Assert.Equal(ResourceProvider.Bytes, await File.ReadAllBytesAsync(downloadedPath));

            var logs = Directory.CreateDirectory(Path.Combine(instance.GameDirectory, "logs"));
            await File.WriteAllTextAsync(Path.Combine(logs.FullName, "latest.log"), "java.lang.OutOfMemoryError: Java heap space\n");
            var crash = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 6, method = "tools/call",
                @params = new { name = "analyze_latest_crash", arguments = new { instanceId = instance.Id } }
            });
            using var analyzed = await RpcAsync(client, page.Endpoint, mcp.Token, crash);
            using var analysis = ToolOutput(analyzed);
            Assert.Contains(analysis.RootElement.GetProperty("findings").EnumerateArray(), item => item.GetProperty("cause").GetString() == "OutOfMemory");

            await page.PauseCommand.ExecuteAsync(null);
            Assert.False(page.IsRunning);
            await Assert.ThrowsAsync<HttpRequestException>(async () => { using var response = await SendAsync(client, page.Endpoint, initialize, mcp.Token); });
            Assert.True(page.CanEditPort);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static JsonDocument ToolOutput(JsonDocument rpc)
    {
        var result = rpc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
        return JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string endpoint, string json, string? token = null,
        string? origin = null, string? host = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Mcp-Protocol-Version", "2025-11-25");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> RpcAsync(HttpClient client, string url, string token, string json)
    {
        using var response = await SendAsync(client, url, json, token);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} {payload}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            payload = payload.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        return JsonDocument.Parse(payload);
    }

    private sealed class ResourceProvider : HttpMessageHandler
    {
        public static readonly byte[] Bytes = Encoding.UTF8.GetBytes("verified resource fixture");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            HttpContent content = path.EndsWith("/fixture-release", StringComparison.Ordinal)
                ? new StringContent(JsonSerializer.Serialize(new
                {
                    id = "fixture-release", project_id = "fixture-project", name = "Fixture",
                    files = new[] { new { filename = "fixture-pack.zip", url = "https://cdn.modrinth.com/data/fixture-pack.zip",
                        size = Bytes.Length, primary = true, hashes = new { sha1 = Convert.ToHexStringLower(SHA1.HashData(Bytes)) } } }
                }), Encoding.UTF8, "application/json")
                : path.EndsWith("/fixture-pack.zip", StringComparison.Ordinal)
                    ? new ByteArrayContent(Bytes)
                    : throw new InvalidOperationException("Unexpected fixture request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
}
