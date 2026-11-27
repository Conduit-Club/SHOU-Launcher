using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Hash the same bytes being written, including an existing contiguous resume prefix.</summary>
internal sealed class TransferHash(DownloadRequest request) : IDisposable
{
    private readonly IncrementalHash? sha1 = request.Sha1 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private readonly IncrementalHash? sha256 = request.Sha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    internal void Append(ReadOnlySpan<byte> bytes) { sha1?.AppendData(bytes); sha256?.AppendData(bytes); }
    internal async Task ReadPrefixAsync(Stream stream, long length, CancellationToken ct)
    {
        if (length == 0 || sha1 is null && sha256 is null) return;
        stream.Position = 0;
        var buffer = new byte[131072];
        for (long remaining = length; remaining > 0;)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Download resume prefix is incomplete.");
            Append(buffer.AsSpan(0, read)); remaining -= read;
        }
    }
    internal bool Matches(long length)
        => (request.Size is null || request.Size == length)
           && (sha1 is null || Convert.ToHexString(sha1.GetHashAndReset()).Equals(request.Sha1, StringComparison.OrdinalIgnoreCase))
           && (sha256 is null || Convert.ToHexString(sha256.GetHashAndReset()).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase));
    public void Dispose() { sha1?.Dispose(); sha256?.Dispose(); }
}
