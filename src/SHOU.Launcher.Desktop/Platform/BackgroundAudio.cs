using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Desktop.Platform;

public sealed class BackgroundAudio : IDisposable
{
    private readonly string alias = "shou" + Guid.NewGuid().ToString("N");
    private Process? player;
    private bool nativeOpen;
    public bool IsPlaying
    {
        get
        {
            if (OperatingSystem.IsWindows() && nativeOpen) { var mode = new StringBuilder(32); return MciStatus("status " + alias + " mode", mode, 32, IntPtr.Zero) == 0 && mode.ToString() == "playing"; }
            return player is { HasExited: false };
        }
    }
    public void SetVolume(decimal volume)
    { if (OperatingSystem.IsWindows() && nativeOpen) Send("setaudio " + alias + " volume to " + ((int)Math.Clamp(volume * 10, 0, 1000)).ToString(CultureInfo.InvariantCulture)); }
    public void Play(string file, decimal volume = 50)
    {
        Stop();
        if (!Path.IsPathFullyQualified(file) || !File.Exists(file) || Path.GetExtension(file).ToLowerInvariant() is not (".wav" or ".mp3" or ".m4a")) throw new ArgumentException("请选择本机 WAV、MP3 或 M4A 音乐。");
        SHOU.Launcher.Core.IO.SafePath.RejectLinks(file);
        if (OperatingSystem.IsWindows())
        {
            if (file.Contains('"') || Send("open \"" + file + "\" alias " + alias) != 0) throw new IOException("无法打开音乐文件，请检查格式或系统音频组件。");
            nativeOpen = true; SetVolume(volume);
            if (Send("play " + alias) != 0) { Stop(); throw new IOException("无法播放音乐。"); }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var start = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-v"); start.ArgumentList.Add(((double)Math.Clamp(volume, 0, 100) / 100).ToString(CultureInfo.InvariantCulture)); start.ArgumentList.Add(file); player = Process.Start(start) ?? throw new IOException("无法启动系统音频播放器。");
        }
        else throw new PlatformNotSupportedException("当前平台尚未接入音频播放器。");
    }
    public void Stop()
    {
        if (nativeOpen && OperatingSystem.IsWindows()) { Send("close " + alias); nativeOpen = false; }
        if (player is { } owned) { try { if (!owned.HasExited) owned.Kill(); } catch (InvalidOperationException) { } finally { owned.Dispose(); player = null; } }
    }
    public void Dispose() => Stop();
    private static uint Send(string command) => MciSendString(command, IntPtr.Zero, 0, IntPtr.Zero);
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)] private static extern uint MciStatus(string command, StringBuilder result, int length, IntPtr callback);
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)] private static extern uint MciSendString(string command, IntPtr result, int length, IntPtr callback);
}
