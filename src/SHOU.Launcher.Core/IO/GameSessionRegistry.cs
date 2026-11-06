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
    private sealed record Entry(int Pid, long StartTicks);
    private string FileFor(GameInstance instance)
    {
        VerifiedDownloader.RejectLink(directory);
        var canonical = SafePath.Canonical(instance.GameDirectory);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return Path.Combine(Path.GetFullPath(directory), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) + ".json");
    }
    public GameSessionSnapshot? Read(GameInstance instance)
    {
        var path = FileFor(instance);
        if (!File.Exists(path)) return null;
        VerifiedDownloader.RejectLink(path);
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("游戏会话记录过大，已禁止修改该实例。");
        Entry entry;
        try
        {
            entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)) ?? throw new JsonException();
            if (entry.Pid <= 0 || entry.StartTicks <= 0 || entry.StartTicks > DateTime.MaxValue.Ticks) throw new JsonException();
        }
        catch (JsonException) { throw new InvalidDataException("游戏会话记录损坏，已禁止修改该实例；请检查日志。"); }
        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            // Exact OS creation time prevents stale records matching a reused PID.
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != entry.StartTicks) return null;
            return new(instance.Id, entry.Pid, new DateTimeOffset(new DateTime(entry.StartTicks, DateTimeKind.Utc)));
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("无法核验游戏会话身份，已禁止修改该实例。"); }
    }
    public void Register(GameInstance instance, int pid)
    {
        using var process = Process.GetProcessById(pid);
        var entry = new Entry(pid, process.StartTime.ToUniversalTime().Ticks);
        Directory.CreateDirectory(directory);
        var path = FileFor(instance); VerifiedDownloader.RejectLink(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entry)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void EnsureWritable(GameInstance instance)
    {
        if (Read(instance) is not null) throw new InvalidOperationException("此实例的游戏仍在运行。请先在游戏内退出，再修改或重新启动此实例；其它实例可以继续使用。");
    }
}
