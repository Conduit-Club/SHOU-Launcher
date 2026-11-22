using System.Collections.Concurrent;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Tests;

/// <summary>Portable service fixtures use explicit credentials and memory, independent of native host adapters.</summary>
internal static class TestPlatformServices
{
    public static PlatformServices Create(PlatformOptions options, IVerifiedDownloader downloader)
        => PlatformServicesFactory.Create(options, downloader, new InMemoryTestCredentials())
            with { MemoryInfo = new FixedTestMemory() };

    private sealed class FixedTestMemory : IMemoryInfo
    {
        public MemoryStatus Read() => new(16L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024);
    }
}

internal sealed class InMemoryTestCredentials : ICredentialStore
{
    private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
    }

    public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
