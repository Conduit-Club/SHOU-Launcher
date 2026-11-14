using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.IO;

/// <summary>Nonsecret process identity. Never authorizes killing a process read from disk.</summary>
public sealed class GameSessionRegistry(string directory)
{
    private sealed record Entry(int Pid, long StartTicks, string? InstanceId = null);
    private string FileFor(GameInstance instance)
    {
        VerifiedDownloader.RejectLink(directory);
        var canonical = SafePath.Canonical(instance.GameDirectory);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return Path.Combine(Path.GetFullPath(directory), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) + ".json");
    }
    public GameSessionSnapshot? Read(GameInstance instance) => ReadAll(instance).LastOrDefault();
    public IReadOnlyList<GameSessionSnapshot> ReadAll(GameInstance instance)
    {
        var path = FileFor(instance);
        if (!File.Exists(path)) return [];
        VerifiedDownloader.RejectLink(path);
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("游戏会话记录过大，已禁止修改该实例。");
        Entry[] entries;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            entries = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.Deserialize<Entry[]>() ?? throw new JsonException()
                : [document.RootElement.Deserialize<Entry>() ?? throw new JsonException()];
            if (entries.Length > 48 || entries.Any(e => e is null || e.Pid <= 0 || e.StartTicks <= 0 || e.StartTicks > DateTime.MaxValue.Ticks)) throw new JsonException();
        }
        catch (JsonException) { throw new InvalidDataException("游戏会话记录损坏，已禁止修改该实例；请检查日志。"); }
        var active = new List<GameSessionSnapshot>();
        foreach (var entry in entries)
        {
            try
            {
                using var process = Process.GetProcessById(entry.Pid);
                // Exact OS creation time prevents stale records matching a reused PID.
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != entry.StartTicks) continue;
                active.Add(new(entry.InstanceId ?? instance.Id, entry.Pid, new DateTimeOffset(new DateTime(entry.StartTicks, DateTimeKind.Utc))));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { throw new IOException("无法核验游戏会话身份，已禁止修改该实例。"); }
        }
        return active.DistinctBy(s => s.ProcessId).OrderBy(s => s.StartedAt).ToArray();
    }
    public void Register(GameInstance instance, int pid)
    {
        using var process = Process.GetProcessById(pid);
        var entry = new Entry(pid, process.StartTime.ToUniversalTime().Ticks, instance.Id);
        Directory.CreateDirectory(directory);
        var path = FileFor(instance); VerifiedDownloader.RejectLink(path);
        var lockPath = path + ".lock"; VerifiedDownloader.RejectLink(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = ReadAll(instance).Where(s => s.ProcessId != pid).Select(s => new Entry(s.ProcessId, s.StartedAt.UtcTicks, s.InstanceId)).Append(entry).ToArray();
        if (entries.Length > 48) throw new InvalidOperationException("此目录的游戏进程过多，请先关闭部分窗口。");
        var payload = JsonSerializer.Serialize(entries);
        if (Encoding.UTF8.GetByteCount(payload) > 4096) throw new InvalidOperationException("游戏会话记录已达到容量上限，请先关闭部分窗口。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, payload); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void EnsureWritable(GameInstance instance)
    {
        if (Read(instance) is not null) throw new InvalidOperationException("此实例仍有游戏窗口运行。请退出这些窗口后再修改实例；其它实例可以继续使用。");
    }
}
