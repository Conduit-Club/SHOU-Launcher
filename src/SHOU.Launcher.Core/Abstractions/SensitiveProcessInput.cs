using System.Security.Cryptography;

namespace SHOU.Launcher.Core.Abstractions;

/// <summary>One-use private stdin bytes. The runner must zero the consumed array in finally.</summary>
public sealed class SensitiveProcessInput : IDisposable
{
    public const int MaximumPayloadBytes = 1_048_590;
    private byte[]? bytes;
    public int ByteLength { get; }
    public TimeSpan TransferTimeout { get; }
    private SensitiveProcessInput(byte[] owned, TimeSpan timeout)
    { bytes = owned; ByteLength = owned.Length; TransferTimeout = timeout; }
    public static SensitiveProcessInput CopyFrom(ReadOnlySpan<byte> payload, TimeSpan? transferTimeout = null)
    {
        if (payload.IsEmpty || payload.Length > MaximumPayloadBytes) throw new ArgumentException("Private process input is empty or exceeds the bounded protocol.", nameof(payload));
        var timeout = transferTimeout ?? TimeSpan.FromSeconds(15);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(transferTimeout));
        return new(payload.ToArray(), timeout);
    }
    public byte[] ConsumeOwnedBytes() => Interlocked.Exchange(ref bytes, null)
        ?? throw new InvalidOperationException("Private process input is already consumed or disposed.");
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref bytes, null);
        if (owned is not null) CryptographicOperations.ZeroMemory(owned);
    }
    public override string ToString() => "SensitiveProcessInput [redacted]";
}
