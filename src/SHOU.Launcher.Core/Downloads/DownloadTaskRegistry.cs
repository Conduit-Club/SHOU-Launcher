using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SHOU.Launcher.Core.Downloads;

public sealed record DownloadOwnership(string InstanceId, string Generation);
public sealed class DownloadTaskRegistry
{
    private readonly ConcurrentDictionary<string, DownloadOwnership> owners = new(StringComparer.Ordinal);
    private DownloadOwnership? active;
    public DownloadOwnership Begin(string instanceId) => active = new(instanceId, Guid.NewGuid().ToString("N"));
    public string Register(string transferId)
    {
        var current = active ?? throw new InvalidOperationException("No active installation owns this transfer.");
        var id = current.Generation + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transferId)))[..16].ToLowerInvariant();
        owners[id] = current; return id;
    }
    public DownloadOwnership Resolve(string taskId) => owners.TryGetValue(taskId, out var owner) ? owner : throw new KeyNotFoundException("找不到此下载任务的所属实例。");
    public void RequireActive(string taskId)
    {
        var owner = Resolve(taskId);
        if (active?.Generation != owner.Generation) throw new InvalidOperationException("此下载任务已结束，不能取消另一轮安装。");
    }
    public void End(DownloadOwnership owner) { if (active?.Generation == owner.Generation) active = null; }
}
