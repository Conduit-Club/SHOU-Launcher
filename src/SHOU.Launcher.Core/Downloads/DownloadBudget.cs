
namespace SHOU.Launcher.Core.Downloads;

/// <summary>One shared budget across verified transfers; cancellation also applies while queued or throttled.</summary>
public sealed class DownloadBudget
{
    private readonly object sync = new();
    private int active;
    private readonly SemaphoreSlim bandwidth = new(1, 1);
    public async Task<IDisposable> EnterAsync(int maximum, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (sync) if (active < maximum) { active++; return new Lease(this); }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }
    public async Task ThrottleAsync(int count, int kibPerSecond, CancellationToken ct)
    {
        if (kibPerSecond <= 0) return;
        await bandwidth.WaitAsync(ct).ConfigureAwait(false);
        try { await Task.Delay(TimeSpan.FromSeconds(count / (kibPerSecond * 1024d)), ct).ConfigureAwait(false); }
        finally { bandwidth.Release(); }
    }
    private sealed class Lease(DownloadBudget owner) : IDisposable
    { private int disposed; public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) lock (owner.sync) owner.active--; } }
}
