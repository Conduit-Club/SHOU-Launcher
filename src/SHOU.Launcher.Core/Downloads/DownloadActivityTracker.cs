using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Incremental full-session accounting. Replacing a task never double-counts it.
/// History stores at most 50 operation summaries; detail-row eviction does not affect totals.</summary>
internal sealed class DownloadActivityTracker
{
    private readonly object sync = new();
    private readonly Accumulator session = new();
    private Accumulator batch = new();
    private DownloadActivitySnapshot? activity;
    private DownloadActivitySnapshot? lastPublished;
    private readonly List<DownloadActivitySnapshot> history = [];
    internal void Begin(string name)
    {
        lock (sync)
        {
            batch = new();
            activity = new(Guid.NewGuid().ToString("N"), name, "Running", DateTimeOffset.UtcNow, null, new());
        }
    }
    internal void Report(DownloadTaskSnapshot task)
    {
        lock (sync)
        {
            session.Report(task); batch.Report(task);
            if (activity is not null) lastPublished = activity = activity with { Statistics = batch.Value };
        }
    }
    internal void Complete(string state)
    {
        lock (sync)
        {
            if (activity is not null && batch.Value.Files > 0)
            {
                lastPublished = activity with { State = state, EndedAt = DateTimeOffset.UtcNow, Statistics = batch.Value };
                history.Add(lastPublished); if (history.Count > 50) history.RemoveAt(0);
            }
            activity = null;
        }
    }
    internal (DownloadStatistics Totals, DownloadActivitySnapshot? Activity, IReadOnlyList<DownloadActivitySnapshot> History) Capture()
    { lock (sync) return (session.Value, lastPublished, history.AsEnumerable().Reverse().ToArray()); }

    private sealed class Accumulator
    {
        private readonly Dictionary<string, DownloadTaskSnapshot> tasks = new(StringComparer.Ordinal);
        internal DownloadStatistics Value { get; private set; } = new();
        internal void Report(DownloadTaskSnapshot task)
        {
            if (tasks.TryGetValue(task.Id, out var previous)) Adjust(previous, -1);
            tasks[task.Id] = task; Adjust(task, 1);
        }
        private void Adjust(DownloadTaskSnapshot task, int sign)
        {
            var pending = task.State is "Queued" or "Downloading" or "Verifying" or "Retrying";
            var total = task.TotalBytes is >= 0 ? task.TotalBytes : task.State == "Completed" ? Math.Max(0, task.BytesReceived) : (long?)null;
            var ready = Math.Max(0, task.BytesReceived); if (total is { } maximum) ready = Math.Min(ready, maximum);
            int Count(bool flag) => flag ? sign : 0;
            Value = Value with
            {
                Files = Value.Files + sign, Completed = Value.Completed + Count(task.State == "Completed"),
                Failed = Value.Failed + Count(task.State == "Failed"), Cancelled = Value.Cancelled + Count(task.State == "Cancelled"),
                Pending = Value.Pending + Count(pending), Downloading = Value.Downloading + Count(task.State == "Downloading"),
                Verifying = Value.Verifying + Count(task.State == "Verifying"), UnknownSizes = Value.UnknownSizes + Count(total is null),
                ReadyBytes = Value.ReadyBytes + sign * ready, KnownTotalBytes = Value.KnownTotalBytes + sign * (total ?? 0),
                CachedBytes = Value.CachedBytes + (task.State == "Completed" && task.Source == "cache" ? sign * ready : 0),
                Connections = Value.Connections + (task.State == "Downloading" ? sign * Math.Clamp(task.Connections, 0, 64) : 0)
            };
        }
    }
}
