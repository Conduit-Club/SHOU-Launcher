namespace SHOU.Launcher.Core.Abstractions;

/// <summary>File-task totals for this launcher session, independent of the bounded detail list.
/// Ready bytes include resumed data and cache hits; they are not wire-traffic counters.</summary>
public sealed record DownloadStatistics(int Files = 0, int Completed = 0, int Failed = 0, int Cancelled = 0,
    int Pending = 0, int Downloading = 0, int Verifying = 0, int UnknownSizes = 0,
    long ReadyBytes = 0, long KnownTotalBytes = 0, long CachedBytes = 0, int Connections = 0);

public sealed record DownloadActivitySnapshot(string Id, string Name, string State, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, DownloadStatistics Statistics);
