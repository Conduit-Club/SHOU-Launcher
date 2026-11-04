using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PlainToolkit.CngProtectedData;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform.Windows;

/// <summary>Reads original UserKey.bin/fields without creation, migration, quarantine, persistence or key rotation.</summary>
public sealed class WindowsLegacyCredentialReader
{
    private static readonly byte[] Entropy = "PCL CE Encryption Key"u8.ToArray();
    private readonly Func<uint, byte[], byte[]> _unprotect;
    private readonly Func<string?> _cpuId;
    public string UserKeyPath { get; }

    public WindowsLegacyCredentialReader(string userKeyPath, Func<uint, byte[], byte[]>? unprotect = null,
        Func<string?>? cpuId = null)
    {
        if (!Path.IsPathFullyQualified(userKeyPath)) throw new ArgumentException("The legacy key path must be absolute.");
        UserKeyPath = Path.GetFullPath(userKeyPath);
        _unprotect = unprotect ?? NativeUnprotect;
        _cpuId = cpuId ?? NativeCpuId;
    }

    /// <summary>Null means absent only. Returned key bytes belong to the caller and should be zeroed after use.</summary>
    public async Task<byte[]?> ReadUserKeyAsync(CancellationToken cancellationToken = default)
    {
        byte[] data;
        try
        {
            await using var stream = new FileStream(UserKeyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length is < 12 or > 1048588) throw new CredentialStoreException(CredentialFailure.Corrupt, "旧 UserKey.bin 大小无效；原文件保留。");
            data = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(data, cancellationToken);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException ex) { throw new CredentialStoreException(CredentialFailure.Denied, "没有读取旧 UserKey.bin 的权限。", ex); }
        catch (IOException ex) { throw new CredentialStoreException(CredentialFailure.IOFailure, "旧 UserKey.bin 暂不可读；不会轮换密钥或改名原文件。", ex); }
        var envelope = LegacyFieldDecryptor.Envelope(data);
        if (envelope.Version is not (1 or 2))
            throw new CredentialStoreException(CredentialFailure.Corrupt, "旧密钥包装版本未知；原 UserKey.bin 保留。");
        if (envelope.Payload.Length == 0)
            throw new CredentialStoreException(CredentialFailure.Corrupt, "旧 DPAPI 密钥包装为空；原 UserKey.bin 保留。");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = _unprotect(envelope.Version, envelope.Payload);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new CredentialStoreException(CredentialFailure.Corrupt, "旧用户密钥长度无效；不会生成替代密钥。");
        }
        catch (CredentialStoreException) { throw; }
        catch (Exception ex) when (ex is PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { throw new CredentialStoreException(CredentialFailure.Unavailable, "当前环境不能解开 Windows DPAPI 旧密钥；原文件保留。", ex); }
        catch (UnauthorizedAccessException ex) { throw new CredentialStoreException(CredentialFailure.Denied, "没有解开旧用户密钥的权限。", ex); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or System.ComponentModel.Win32Exception)
        { throw new CredentialStoreException(CredentialFailure.Corrupt, "DPAPI 无法解开旧用户密钥；不会轮换、迁移或覆盖原文件。", ex); }
    }

    /// <summary>Failure is terminal for this read. The importer must disable persistence rather than save an empty profile list.</summary>
    public async Task<string> DecryptFieldAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(value)) return "";
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? key = null;
        try
        {
            if (LegacyFieldDecryptor.UsesUserKey(value))
            {
                key = await ReadUserKeyAsync(cancellationToken)
                    ?? throw new CredentialStoreException(CredentialFailure.Unavailable, "缺少旧 UserKey.bin；无法读取原加密字段，不能创建替代密钥。");
                return LegacyFieldDecryptor.Decrypt(value, key);
            }
            var password = await Task.Run(() => LegacyFieldDecryptor.HistoricalPasswordFromCpuId(_cpuId()), cancellationToken);
            return LegacyFieldDecryptor.Decrypt(value, historicalPassword: password);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] NativeUnprotect(uint version, byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return version switch
        {
            1 => ProtectedData.Unprotect(data, Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser),
            2 => CngProtectedData.Unprotect(data, Entropy, PlainToolkit.CngProtectedData.DataProtectionScope.CurrentUser),
            _ => throw new CryptographicException("Unsupported original UserKey.bin wrapper version.")
        };
    }

    private static string? NativeCpuId()
    {
        if (!OperatingSystem.IsWindows()) throw new CredentialStoreException(CredentialFailure.Unavailable, "历史 CPU 派生凭据只能在原 Windows 环境读取。");
        object? locator = null, services = null, processors = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
            locator = Activator.CreateInstance(type);
            services = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            processors = ((dynamic)services!).ExecQuery("SELECT ProcessorId FROM Win32_Processor");
            foreach (object processor in (dynamic)processors!)
            {
                try { return ((dynamic)processor).ProcessorId?.ToString(); }
                finally { ReleaseCom(processor); }
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Reflection.TargetInvocationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Same historic default raw-code path as IdentifyOld; a wrong fallback still fails decryption, never writes.
            return null;
        }
        finally { ReleaseCom(processors); ReleaseCom(services); ReleaseCom(locator); }
    }
    private static void ReleaseCom(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
