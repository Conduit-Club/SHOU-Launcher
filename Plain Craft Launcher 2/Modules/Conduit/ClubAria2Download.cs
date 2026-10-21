using System.IO;
using System.Net.Http;
using PCL.Core.App;
using PCL.Core.Conduit;
using PCL.Network;

namespace PCL;

public static class ClubAria2Download
{
    public static async Task DownloadAsync(IReadOnlyList<string> urls, string localPath, bool browserAgent, string userAgent,
        DownloadFile? file, CancellationToken cancellationToken)
    {
        var partial = localPath + ".aria2.part";
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, urls[0]);
        RequestSigning.SecretHeadersSign(urls[0], ref request, browserAgent, userAgent);
        userAgent = request.Headers.UserAgent.ToString();
        request.Dispose();
        var rpc = new Aria2RpcClient(http, Aria2RpcClient.ParseEndpoint(Config.Download.Aria2Endpoint), Config.Download.Aria2Secret);
        await rpc.DownloadAsync(urls, partial, userAgent, status =>
        {
            if (file == null) return;
            file.State = NetState.Downloading;
            file.DownloadedBytes = status.Completed;
            file.TotalSize = status.Total;
            file.IsUnknownSize = status.Total <= 0;
            file.Speed = status.Speed;
            file.ActiveThreads = status.Connections;
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var error = file?.Check == null ? null : await Task.Run(() => file.Check.Check(partial), cancellationToken).ConfigureAwait(false);
        if (error != null)
        {
            // Retain the invalid file for diagnosis but never resume its corrupt bytes.
            File.Move(partial, partial + ".invalid-" + Guid.NewGuid().ToString("N"));
            if (File.Exists(partial + ".aria2")) File.Move(partial + ".aria2", partial + ".aria2.invalid-" + Guid.NewGuid().ToString("N"));
            throw new IOException("aria2 下载文件校验失败：" + error);
        }
        File.Move(partial, localPath, overwrite: true);
        try { await rpc.ForgetCompletedAsync(partial, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { ModBase.Log(ex, "[aria2] 完成记录清理失败，不影响已校验文件", ModBase.LogLevel.Debug); }
        if (file == null) return;
        file.TotalSize = new FileInfo(localPath).Length;
        file.DownloadedBytes = file.TotalSize;
        file.Speed = 0;
        file.ActiveThreads = 0;
        file.State = NetState.Finished;
    }
}
