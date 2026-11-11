using System.Diagnostics;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Trailing one-second network bytes across short and parallel transfers; cache hits and resume offsets are excluded.</summary>
internal sealed class DownloadRateMeter(Func<TimeSpan>? clock = null)
{
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private readonly object sync = new();
    private readonly Dictionary<string, long> received = new(StringComparer.Ordinal);
    private readonly Queue<(TimeSpan At, long Bytes)> samples = new();
    private long bytes;
    internal double Report(TransferProgress progress)
    {
        lock (sync)
        {
            var now = clock?.Invoke() ?? timer.Elapsed;
            while (samples.TryPeek(out var old) && now - old.At >= TimeSpan.FromSeconds(1)) { bytes -= samples.Dequeue().Bytes; }
            var previous = received.GetValueOrDefault(progress.Id);
            var network = progress.Source != "cache" && (progress.Stage == "Downloading" || progress.Stage == "Completed" && !string.IsNullOrWhiteSpace(progress.Source));
            var delta = network ? Math.Max(0, progress.BytesReceived - previous) : 0;
            if (delta > 0) { samples.Enqueue((now, delta)); bytes += delta; }
            if (progress.Stage is "Completed" or "Cancelled" or "Failed") received.Remove(progress.Id);
            else received[progress.Id] = Math.Max(0, progress.BytesReceived);
            return bytes;
        }
    }
    internal void Reset() { lock (sync) { received.Clear(); samples.Clear(); bytes = 0; } }
}
