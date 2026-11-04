using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform;

/// <summary>Runs only explicit executables; shell scripts are a separate caller decision.</summary>
public sealed class ProcessRunner : IProcessRunner, IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, ManagedProcess> _owned = new();
    private int _disposed;

    public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(spec.FileName) || !Path.IsPathFullyQualified(spec.WorkingDirectory))
            throw new ArgumentException("Process filename and working directory must be absolute.");
        if (!Directory.Exists(spec.WorkingDirectory)) throw new DirectoryNotFoundException("The process working directory does not exist.");
        if (spec.OutputLimitCharacters is <= 0 or > 16777216)
            throw new ArgumentOutOfRangeException(nameof(spec), "The capture limit must be between 1 and 16777216 characters.");
        if (spec.Timeout is { } timeout && timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(spec), "The timeout must be positive.");

        var start = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);
        if (spec.EnvironmentOverrides is not null)
            foreach (var (key, value) in spec.EnvironmentOverrides)
            {
                if (value is null) start.Environment.Remove(key);
                else start.Environment[key] = value;
            }

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The operating system did not start the process.");
        }
        catch { process.Dispose(); throw; }

        var managed = new ManagedProcess(process, spec, output, cancellationToken);
        _owned[managed.ProcessId] = managed;
        _ = managed.Completion.ContinueWith(_ => _owned.TryRemove(managed.ProcessId, out var removed),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (Volatile.Read(ref _disposed) != 0)
            return DisposeRejectedStartAsync(managed);
        return Task.FromResult<IManagedProcess>(managed);
    }

    private static async Task<IManagedProcess> DisposeRejectedStartAsync(ManagedProcess process)
    {
        await process.DisposeAsync();
        throw new ObjectDisposedException(nameof(ProcessRunner));
    }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null,
        CancellationToken cancellationToken = default)
    {
        await using var process = await StartAsync(spec, output, cancellationToken);
        return await process.Completion;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Task.WhenAll(_owned.Values.Select(async process => await process.DisposeAsync()));
    }

    private sealed class ManagedProcess : IManagedProcess
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _captureLock = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly int _limit;
        private readonly IProgress<ProcessOutput>? _output;
        private int _captured;
        private bool _truncated;
        private int _disposed;
        public int ProcessId { get; }
        public Task<ProcessResult> Completion { get; }

        public ManagedProcess(Process process, ProcessSpec spec, IProgress<ProcessOutput>? output, CancellationToken cancellation)
        {
            _process = process;
            ProcessId = process.Id;
            _limit = spec.OutputLimitCharacters;
            _output = output;
            Completion = ObserveAsync(spec.Timeout, cancellation);
        }

        private async Task ReadAsync(StreamReader reader, bool standardError)
        {
            var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory());
                    if (count == 0) break;
                    string? captured = null;
                    lock (_captureLock)
                    {
                        var allowed = Math.Min(count, _limit - _captured);
                        if (allowed < count) _truncated = true;
                        if (allowed > 0)
                        {
                            captured = new string(buffer, 0, allowed);
                            (standardError ? _stderr : _stdout).Append(captured);
                            _captured += allowed;
                        }
                    }
                    // Continue draining both pipes after the bound, but do not enqueue unlimited UI callbacks.
                    if (captured is not null && _output is not null)
                    {
                        try { _output.Report(new ProcessOutput(DateTimeOffset.UtcNow, standardError, captured)); }
                        catch { /* A presentation callback must not leave the child blocked on a full pipe. */ }
                    }
                }
            }
            catch (ObjectDisposedException) { lock (_captureLock) _truncated = true; }
            catch (IOException) { lock (_captureLock) _truncated = true; }
        }

        private async Task<ProcessResult> ObserveAsync(TimeSpan? timeout, CancellationToken cancellation)
        {
            using var deadline = new CancellationTokenSource();
            if (timeout is { } value && value != Timeout.InfiniteTimeSpan) deadline.CancelAfter(value);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token, deadline.Token);
            var reads = Task.WhenAll(ReadAsync(_process.StandardOutput, false), ReadAsync(_process.StandardError, true));
            var timedOut = false;
            var cancelled = false;
            try
            {
                await _process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                cancelled = cancellation.IsCancellationRequested || _stop.IsCancellationRequested;
                timedOut = deadline.IsCancellationRequested && !cancelled;
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (_process.HasExited) { }
                await _process.WaitForExitAsync();
            }
            finally
            {
                // A descendant may retain a redirected pipe after the parent exits. Bound that drain too.
                try { await reads.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException)
                {
                    _process.StandardOutput.Dispose();
                    _process.StandardError.Dispose();
                    lock (_captureLock) _truncated = true;
                }
            }
            lock (_captureLock)
                return new ProcessResult(_process.ExitCode, _stdout.ToString(), _stderr.ToString(), timedOut, cancelled, _truncated);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!Completion.IsCompleted) _stop.Cancel();
            await Completion.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await StopAsync(); }
            finally { _process.Dispose(); _stop.Dispose(); }
        }
    }
}
