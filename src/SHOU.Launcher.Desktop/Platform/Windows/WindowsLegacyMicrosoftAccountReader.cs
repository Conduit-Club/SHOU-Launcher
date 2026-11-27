using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform.Windows;

/// <summary>Explicit, read-only compatibility with the current user's old Profile container.</summary>
public sealed class WindowsLegacyMicrosoftAccountReader : ILegacyMicrosoftAccountReader
{
    private readonly string configPath;
    private readonly Func<string, CancellationToken, Task<string>> decrypt;

    public WindowsLegacyMicrosoftAccountReader(string directory)
        : this(directory, new WindowsLegacyCredentialReader(Path.Combine(directory, "UserKey.bin")).DecryptFieldAsync) { }

    public WindowsLegacyMicrosoftAccountReader(string directory, Func<string, CancellationToken, Task<string>> decrypt)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("旧账户目录必须是绝对路径。");
        configPath = Path.Combine(directory, "config.v1.json");
        this.decrypt = decrypt;
    }

    public async Task<IReadOnlyList<LegacyMicrosoftAccountCredential>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] data;
            try
            {
                await using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (stream.Length is <= 0 or > 2_000_000) throw Invalid();
                data = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            try
            {
                ReadOnlyMemory<byte> json = data;
                if (data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
                using var config = JsonDocument.Parse(json, new() { MaxDepth = 32 });
                if (!config.RootElement.TryGetProperty("Profile", out var field)) return [];
                if (field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: > 0 and <= 1_500_000 } encoded) throw Invalid();
                var plaintext = await decrypt(encoded, cancellationToken).ConfigureAwait(false);
                if (plaintext.Length > 1_500_000) throw Invalid();
                using var document = JsonDocument.Parse(plaintext, new() { MaxDepth = 32 });
                if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() > 128) throw Invalid();
                var result = new List<LegacyMicrosoftAccountCredential>();
                foreach (var entry in profiles.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.ValueKind != JsonValueKind.Object) throw Invalid();
                    if (!entry.TryGetProperty("profileType", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Microsoft") continue;
                    // Never interpret a third-party skin provider's token as a Microsoft token.
                    if (!entry.TryGetProperty("provider", out var provider) || provider.ValueKind != JsonValueKind.String || provider.GetString() != "microsoft") continue;
                    var id = String(entry, "profileId", 128);
                    var name = String(entry, "username", 64);
                    var uuid = String(entry, "uuid", 36);
                    var refresh = String(entry, "refreshToken", 32_768);
                    if (!Guid.TryParse(uuid, out var parsed) || parsed == Guid.Empty) throw Invalid();
                    if (string.IsNullOrWhiteSpace(refresh)) continue; // Standard OAuth remains available.
                    var normalized = parsed.ToString("N");
                    var sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\0" + normalized))).ToLowerInvariant();
                    if (result.Any(item => item.SourceId == sourceId)) throw Invalid();
                    result.Add(new(sourceId, name, normalized, refresh));
                }
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (CredentialStoreException) { throw; }
        catch (UnauthorizedAccessException) { throw new CredentialStoreException(CredentialFailure.Denied, "无法读取旧账户；原文件保留，可使用微软官网授权登录。"); }
        catch (IOException) { throw new CredentialStoreException(CredentialFailure.IOFailure, "旧账户暂不可读；原文件保留，请重试或使用微软官网授权。"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { throw Invalid(); } // Do not propagate parser text, paths or secret field contents.
    }

    private static string String(JsonElement entry, string key, int limit)
    {
        if (!entry.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > limit || text.Any(char.IsControl)) throw Invalid();
        return text;
    }
    private static CredentialStoreException Invalid() => new(CredentialFailure.Corrupt, "旧账户格式无法安全识别；原文件保留，请使用微软官网授权。");
}
