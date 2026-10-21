using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;

namespace PCL.Core.Test.Conduit;

[TestClass]
public sealed class Aria2RpcClientTest
{
    private sealed class Handler(Func<JsonNode, JsonNode> respond) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Requests.Add(body);
            var response = respond(body);
            response["id"] = (string)body["id"]!;
            response["jsonrpc"] = "2.0";
            return new HttpResponseMessage(response["error"] == null ? HttpStatusCode.OK : HttpStatusCode.BadRequest) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private static JsonObject Result(JsonNode result) => new() { ["result"] = result };
    private static JsonObject Missing() => new() { ["error"] = new JsonObject { ["code"] = 1, ["message"] = "GID not found" } };

    [TestMethod]
    public async Task SecretIsFirstParameterAndErrorsDoNotEchoIt()
    {
        using var handler = new Handler(_ => new JsonObject { ["error"] = new JsonObject { ["code"] = 1, ["message"] = "secret-do-not-log" } });
        using var http = new HttpClient(handler);
        var rpc = new Aria2RpcClient(http, new Uri("http://127.0.0.1:6800/jsonrpc"), "secret-do-not-log");
        var exception = await Assert.ThrowsAsync<Aria2RpcException>(() => rpc.GetVersionAsync());
        Assert.IsFalse(exception.Message.Contains("secret-do-not-log"));
        Assert.AreEqual("token:secret-do-not-log", (string)handler.Requests[0]["params"]![0]!);
    }

    [TestMethod]
    public async Task NewTaskUsesOrderedMirrorsAndResumableStablePath()
    {
        var path = Path.GetTempFileName();
        try
        {
            var polls = 0;
            using var handler = new Handler(body => (string?)body["method"] switch
            {
                "aria2.tellStatus" when polls++ == 0 => Missing(),
                "aria2.tellStatus" => Result(new JsonObject { ["status"] = "complete", ["completedLength"] = "20", ["totalLength"] = "20", ["downloadSpeed"] = "0", ["connections"] = "0" }),
                "aria2.addUri" => Result(JsonValue.Create("gid")!),
                _ => throw new Exception("unexpected RPC")
            });
            using var http = new HttpClient(handler);
            var rpc = new Aria2RpcClient(http, new Uri("http://127.0.0.1:6800/jsonrpc"), "");
            Aria2Progress? progress = null;
            await rpc.DownloadAsync(["https://first.test/a", "https://second.test/a"], path, "test-agent", p => progress = p);
            var args = handler.Requests[1]["params"]!;
            Assert.AreEqual("https://first.test/a", (string)args[0]![0]!);
            Assert.AreEqual("https://second.test/a", (string)args[0]![1]!);
            Assert.AreEqual("true", (string)args[1]!["continue"]!);
            Assert.AreEqual("false", (string)args[1]!["auto-file-renaming"]!);
            Assert.AreEqual("inorder", (string)args[1]!["uri-selector"]!);
            Assert.AreEqual(Aria2RpcClient.TaskId(path), (string)args[1]!["gid"]!);
            Assert.AreEqual(20L, progress!.Completed);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task CancellationPausesOnlyOwnedTask()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".part");
        using var cancellation = new CancellationTokenSource();
        var polls = 0;
        using var handler = new Handler(body => (string?)body["method"] switch
        {
            "aria2.tellStatus" when polls++ == 0 => Missing(),
            "aria2.tellStatus" => Result(new JsonObject { ["status"] = "active" }),
            _ => Result(JsonValue.Create("OK")!)
        });
        using var http = new HttpClient(handler);
        var rpc = new Aria2RpcClient(http, new Uri("http://127.0.0.1:6800/jsonrpc"), "");
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => rpc.DownloadAsync(["https://test/a"], path, "", _ => cancellation.Cancel(), cancellation.Token));
        Assert.AreEqual("aria2.forcePause", (string)handler.Requests[^1]["method"]!);
        Assert.AreEqual(Aria2RpcClient.TaskId(path), (string)handler.Requests[^1]["params"]![0]!);
    }

    [TestMethod]
    public async Task ExistingPausedJobIsReattachedWithoutCreatingDuplicate()
    {
        var path = Path.GetTempFileName();
        try
        {
            var polls = 0;
            using var handler = new Handler(body => (string?)body["method"] == "aria2.tellStatus"
                ? Result(new JsonObject { ["status"] = polls++ == 0 ? "paused" : "complete", ["files"] = new JsonArray(new JsonObject { ["path"] = path }) })
                : Result(JsonValue.Create("OK")!));
            using var http = new HttpClient(handler);
            var rpc = new Aria2RpcClient(http, new Uri("http://127.0.0.1:6800/jsonrpc"), "");
            await rpc.DownloadAsync(["https://test/a"], path, "", null);
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual("aria2.unpause", (string)handler.Requests[1]["method"]!);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task OutputPathMismatchRefusesToTakeOverJob()
    {
        using var handler = new Handler(_ => Result(new JsonObject { ["status"] = "paused", ["files"] = new JsonArray(new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "unrelated.part") }) }));
        using var http = new HttpClient(handler);
        var rpc = new Aria2RpcClient(http, new Uri("http://127.0.0.1:6800/jsonrpc"), "");
        await Assert.ThrowsExactlyAsync<IOException>(() => rpc.DownloadAsync(["https://test/a"], Path.Combine(Path.GetTempPath(), "owned.part"), "", null));
        Assert.HasCount(1, handler.Requests);
    }
}
