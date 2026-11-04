using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform.Windows;

/// <summary>Read compatibility with PCL.Core/Utils/Secret/EncryptHelper and its original encryption providers.</summary>
public static class LegacyFieldDecryptor
{
    private const uint Magic = 0x454e4321; // ENC!; deliberately different from the new credential store's SHOU header.
    private const string DefaultRawCode = "B09675A9351CBD1FD568056781FE3966DD936CC9B94E51AB5CF67EEB7E74C075";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool UsesUserKey(string encoded) => HasHeader(DecodeBase64(encoded));

    /// <summary>v0 stream cipher and historical CBC have no authentication tag. Their integrity cannot be established.</summary>
    public static bool HasAuthenticationTag(string encoded)
    {
        var bytes = DecodeBase64(encoded);
        if (!HasHeader(bytes)) return false;
        var envelope = Envelope(bytes);
        return envelope.Version is 1 or 2;
    }

    public static string Decrypt(string? encoded, ReadOnlySpan<byte> userKey = default, string? historicalPassword = null)
    {
        if (string.IsNullOrEmpty(encoded)) return "";
        byte[]? plaintext = null;
        try
        {
            var bytes = DecodeBase64(encoded);
            if (HasHeader(bytes))
            {
                if (userKey.Length != 32) throw new CryptographicException("Legacy field requires a 32-byte user key.");
                var envelope = Envelope(bytes);
                plaintext = envelope.Version switch
                {
                    0 => DecryptSoftwareChaCha(envelope.Payload, userKey),
                    1 => DecryptChaChaPoly1305(envelope.Payload, userKey),
                    2 => DecryptAesGcm(envelope.Payload, userKey),
                    _ => throw new CryptographicException("Unsupported legacy field version.")
                };
            }
            else
            {
                if (historicalPassword is null) throw new CryptographicException("Historical AES password is required.");
                plaintext = DecryptHistoricalCbc(bytes, historicalPassword);
            }
            return StrictUtf8.GetString(plaintext);
        }
        catch (CredentialStoreException) { throw; }
        catch (PlatformNotSupportedException ex)
        { throw new CredentialStoreException(CredentialFailure.Unavailable, "本机不支持读取该历史加密算法；原字段保留。", ex); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        { throw new CredentialStoreException(CredentialFailure.Corrupt, "旧凭据字段无法解密；不会按明文返回或覆盖原记录。", ex); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static string HistoricalPasswordFromCpuId(string? cpuId)
    {
        var rawCode = cpuId is null ? DefaultRawCode : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cpuId)));
        return Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(rawCode))).Substring(4, 32);
    }

    internal static (uint Version, byte[] Payload) Envelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !HasHeader(bytes)) throw Corrupt("旧 ENC! 包装头损坏。");
        var count = BinaryPrimitives.ReadInt32BigEndian(bytes[8..]);
        if (count < 0 || count > 1048576 || count != bytes.Length - 12) throw Corrupt("旧 ENC! 包装长度无效。");
        return (BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]), bytes[12..].ToArray());
    }
    private static bool HasHeader(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == Magic;
    private static byte[] DecodeBase64(string encoded)
    {
        try
        {
            if (encoded.Length > 1500000) throw new FormatException("Encoded field exceeds the read budget.");
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == 0x53484f55)
                throw Corrupt("新 SHOU 凭据记录不是旧 ENC! 包装；不能按旧字段读取。");
            return bytes;
        }
        catch (FormatException ex) { throw new CredentialStoreException(CredentialFailure.Corrupt, "旧凭据不是有效的受支持编码；原字段保留。", ex); }
    }
    private static CredentialStoreException Corrupt(string message) => new(CredentialFailure.Corrupt, message);

    private static byte[] DecryptAesGcm(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (data.Length < 28) throw new CryptographicException("Invalid AES-GCM field length.");
        var plaintext = new byte[data.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data[..12], data[28..], data.Slice(12, 16), plaintext);
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }

    private static byte[] DecryptChaChaPoly1305(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (data.Length < 44) throw new CryptographicException("Invalid ChaCha20-Poly1305 field length.");
        Span<byte> derived = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, derived, data[..16], "PCL.Core.Utils.Encryption.ChaCha20"u8);
        var plaintext = new byte[data.Length - 44];
        try
        {
            using var chacha = new ChaCha20Poly1305(derived);
            chacha.Decrypt(data.Slice(16, 12), data[44..], data.Slice(28, 16), plaintext);
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private static byte[] DecryptHistoricalCbc(ReadOnlySpan<byte> data, string password)
    {
        if (data.Length < 64 || (data.Length - 48) % 16 != 0) throw new CryptographicException("Invalid historical CBC length.");
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var derived = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, data[..32], 1000, HashAlgorithmName.SHA1, 32);
        try
        {
            using var aes = Aes.Create();
            aes.Key = derived;
            return aes.DecryptCbc(data[48..], data.Slice(32, 16), PaddingMode.PKCS7);
        }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); CryptographicOperations.ZeroMemory(derived); }
    }

    // Original v0 is ChaCha20 with a 96-bit nonce and counter starting at zero, without Poly1305.
    // This reader retains that precise format; it never writes new v0 ciphertext.
    private static byte[] DecryptSoftwareChaCha(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (data.Length < 12) throw new CryptographicException("Invalid software ChaCha20 field length.");
        var nonce = data[..12]; var ciphertext = data[12..];
        var plaintext = new byte[ciphertext.Length];
        Span<uint> state = stackalloc uint[16];
        Span<uint> block = stackalloc uint[16];
        state[0] = 0x61707865; state[1] = 0x3320646e; state[2] = 0x79622d32; state[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
        state[12] = 0;
        state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);
        state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..]);
        Span<byte> stream = stackalloc byte[64];
        try
        {
            for (var offset = 0; offset < ciphertext.Length; offset += 64)
            {
                state.CopyTo(block);
                for (var round = 0; round < 10; round++)
                {
                    Quarter(block, 0, 4, 8, 12); Quarter(block, 1, 5, 9, 13); Quarter(block, 2, 6, 10, 14); Quarter(block, 3, 7, 11, 15);
                    Quarter(block, 0, 5, 10, 15); Quarter(block, 1, 6, 11, 12); Quarter(block, 2, 7, 8, 13); Quarter(block, 3, 4, 9, 14);
                }
                for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(stream.Slice(i * 4, 4), unchecked(block[i] + state[i]));
                state[12]++;
                for (var i = 0; i < Math.Min(64, ciphertext.Length - offset); i++) plaintext[offset + i] = (byte)(ciphertext[offset + i] ^ stream[i]);
            }
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(state));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(block));
            CryptographicOperations.ZeroMemory(stream);
        }
    }

    private static void Quarter(Span<uint> block, int a, int b, int c, int d)
    {
        unchecked
        {
            block[a] += block[b]; block[d] = BitOperations.RotateLeft(block[d] ^ block[a], 16);
            block[c] += block[d]; block[b] = BitOperations.RotateLeft(block[b] ^ block[c], 12);
            block[a] += block[b]; block[d] = BitOperations.RotateLeft(block[d] ^ block[a], 8);
            block[c] += block[d]; block[b] = BitOperations.RotateLeft(block[b] ^ block[c], 7);
        }
    }
}
