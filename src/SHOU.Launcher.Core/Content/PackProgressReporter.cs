using System.Diagnostics;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Content;

internal sealed class PackProgressReporter(IProgress<PackInstallProgress> target) : IProgress<PackInstallProgress>
{
    private readonly object sync = new();
    private string? stage;
    private int completed;
    private long reported;
    public void Report(PackInstallProgress value)
    {
        lock (sync)
        {
            if (stage == value.Stage && value.CompletedFiles < completed) return;
            var now = Stopwatch.GetTimestamp();
            if (stage == value.Stage && value.CompletedFiles < value.TotalFiles && Stopwatch.GetElapsedTime(reported, now) < TimeSpan.FromMilliseconds(100)) return;
            stage = value.Stage; completed = value.CompletedFiles; reported = now;
            target.Report(value);
        }
    }
}
