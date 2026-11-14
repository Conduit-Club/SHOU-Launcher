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
        var paused = false;
        using var handler = new Handler(body => (string?)body["method"] switch
        {
            "aria2.tellStatus" when polls++ == 0 => Missing(),
            "aria2.tellStatus" => Result(new JsonObject { ["status"] = paused ? "paused" : "active", ["connections"] = "0" }),
            "aria2.pause" => Pause(),
            _ => Result(JsonValue.Create("OK")!)
        });
        JsonObject Pause() { paused = true; return Result(JsonValue.Create("OK")!); }
        using var http = new HttpClient(handler);
        var rpc = new Aria2RpcClient(http, new Uri("http://127.0.0.1:6800/jsonrpc"), "");
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => rpc.DownloadAsync(["https://test/a"], path, "", _ => cancellation.Cancel(), cancellation.Token));
        CollectionAssert.AreEqual(new[] { "aria2.tellStatus", "aria2.addUri", "aria2.tellStatus", "aria2.pause", "aria2.tellStatus", "aria2.tellStatus" },
            handler.Requests.ConvertAll(r => (string)r["method"]!).ToArray());
        foreach (var request in handler.Requests.FindAll(r => (string?)r["method"] != "aria2.addUri"))
            Assert.AreEqual(Aria2RpcClient.TaskId(path), (string)request["params"]![0]!);
    }

    [TestMethod]
    public async Task ExistingPausedJobIsReattachedWithoutCreatingDuplicate()
    {
        var path = Path.GetTempFileName();
        try
        {
            var removed = false;
            var added = false;
            using var handler = new Handler(body => (string?)body["method"] switch
            {
                "aria2.tellStatus" => Result(new JsonObject { ["status"] = added ? "complete" : removed ? "removed" : "paused", ["files"] = new JsonArray(new JsonObject { ["path"] = path }) }),
                "aria2.remove" => Remove(),
                "aria2.addUri" => Add(),
                "aria2.removeDownloadResult" => Result(JsonValue.Create("OK")!),
                _ => throw new InvalidOperationException("Unexpected reattachment RPC.")
            });
            JsonObject Remove() { removed = true; return Result(JsonValue.Create("OK")!); }
            JsonObject Add() { added = true; return Result(JsonValue.Create(Aria2RpcClient.TaskId(path))!); }
            using var http = new HttpClient(handler);
            var rpc = new Aria2RpcClient(http, new Uri("http://127.0.0.1:6800/jsonrpc"), "");
            await rpc.DownloadAsync(["https://test/a"], path, "", null);
            CollectionAssert.AreEqual(new[] { "aria2.tellStatus", "aria2.remove", "aria2.tellStatus", "aria2.removeDownloadResult", "aria2.addUri", "aria2.tellStatus" },
                handler.Requests.ConvertAll(r => (string)r["method"]!).ToArray());
            var options = handler.Requests[4]["params"]![1]!;
            Assert.AreEqual(Aria2RpcClient.TaskId(path), (string)options["gid"]!);
            Assert.AreEqual(Path.GetDirectoryName(path), (string)options["dir"]!);
            Assert.AreEqual(Path.GetFileName(path), (string)options["out"]!);
            Assert.AreEqual("true", (string)options["continue"]!);
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
