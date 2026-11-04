using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform.MacOS;

/// <summary>Generic-password items via Security.framework. Secret bytes never enter a subprocess or argv.</summary>
public sealed class KeychainCredentialStore : ICredentialStore
{
    private readonly string _service;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KeychainCredentialStore(string service)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Keychain requires macOS.");
        ValidateName(service);
        _service = service;
        // No framework initialization or keychain access until the data lease has been acquired by the host.
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateName(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                using var query = Query(key);
                Native.Set(query, "kSecReturnData", Native.True);
                Native.Set(query, "kSecMatchLimit", Native.Constant("kSecMatchLimitOne"));
                var status = Native.SecItemCopyMatching(query.DangerousGetHandle(), out var result);
                using var data = new CFHandle(result);
                if (status == -25300) return null;
                Check(status);
                if (data.IsInvalid || Native.CFGetTypeID(result) != Native.CFDataGetTypeID())
                    throw new CredentialStoreException(CredentialFailure.Corrupt, "Keychain 未返回有效凭据数据。");
                var count = Native.CFDataGetLength(result);
                if (count < 0 || count > 1048576) throw new CredentialStoreException(CredentialFailure.Corrupt, "Keychain 凭据大小无效。");
                var bytes = new byte[(int)count];
                if (bytes.Length > 0) Marshal.Copy(Native.CFDataGetBytePtr(result), bytes, 0, bytes.Length);
                return bytes;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        { throw new CredentialStoreException(CredentialFailure.Unavailable, "本机 Security framework 不可用。", ex); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        ValidateName(key);
        if (value.Length > 1048576) throw new ArgumentOutOfRangeException(nameof(value));
        await _gate.WaitAsync(cancellationToken);
        var bytes = value.ToArray();
        try
        {
            await Task.Run(() =>
            {
                using var query = Query(key);
                using var data = new CFHandle(Native.CFDataCreate(IntPtr.Zero, bytes, bytes.Length));
                using var changes = Native.Dictionary();
                Native.Set(changes, "kSecValueData", data.DangerousGetHandle());
                var status = Native.SecItemUpdate(query.DangerousGetHandle(), changes.DangerousGetHandle());
                if (status == -25300)
                {
                    Native.Set(query, "kSecValueData", data.DangerousGetHandle());
                    status = Native.SecItemAdd(query.DangerousGetHandle(), IntPtr.Zero);
                    if (status == -25299)
                    {
                        using var retry = Query(key);
                        status = Native.SecItemUpdate(retry.DangerousGetHandle(), changes.DangerousGetHandle());
                    }
                }
                Check(status);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        { throw new CredentialStoreException(CredentialFailure.Unavailable, "本机 Security framework 不可用。", ex); }
        finally { CryptographicOperations.ZeroMemory(bytes); _gate.Release(); }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateName(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                using var query = Query(key);
                var status = Native.SecItemDelete(query.DangerousGetHandle());
                if (status != -25300) Check(status);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        { throw new CredentialStoreException(CredentialFailure.Unavailable, "本机 Security framework 不可用。", ex); }
        finally { _gate.Release(); }
    }

    private CFHandle Query(string key)
    {
        var query = Native.Dictionary();
        Native.Set(query, "kSecClass", Native.Constant("kSecClassGenericPassword"));
        using var service = Native.String(_service);
        using var account = Native.String(key);
        Native.Set(query, "kSecAttrService", service.DangerousGetHandle());
        Native.Set(query, "kSecAttrAccount", account.DangerousGetHandle());
        // Do not let a locked or inaccessible keychain turn an unattended operation into an authorization prompt.
        Native.Set(query, "kSecUseAuthenticationUI", Native.Constant("kSecUseAuthenticationUIFail"));
        return query;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Contains('\0'))
            throw new ArgumentException("A nonempty bounded credential identifier is required.");
    }

    private static void Check(int status)
    {
        if (status == 0) return;
        var failure = status switch
        {
            -25308 => CredentialFailure.Locked,
            -25293 or -128 or -34018 => CredentialFailure.Denied,
            -26275 => CredentialFailure.Corrupt,
            _ => CredentialFailure.Unavailable
        };
        throw new CredentialStoreException(failure, $"Keychain 操作失败（OSStatus {status}），凭据未降级保存。");
    }

    private sealed class CFHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CFHandle(IntPtr handle) : base(true) { SetHandle(handle); }
        protected override bool ReleaseHandle() { Native.CFRelease(handle); return true; }
    }

    // Apple Security/SecItem.h and CoreFoundation headers are the ABI authority.
    // https://developer.apple.com/documentation/security/secitemcopymatching(_:_:)
    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly IntPtr SecurityLibrary = NativeLibrary.Load(Security);
        private static readonly IntPtr FoundationLibrary = NativeLibrary.Load(CoreFoundation);
        public static IntPtr Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityLibrary, name));
        public static IntPtr True => Marshal.ReadIntPtr(NativeLibrary.GetExport(FoundationLibrary, "kCFBooleanTrue"));
        public static CFHandle Dictionary()
        {
            var handle = new CFHandle(CFDictionaryCreateMutable(IntPtr.Zero, 0,
                NativeLibrary.GetExport(FoundationLibrary, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(FoundationLibrary, "kCFTypeDictionaryValueCallBacks")));
            if (handle.IsInvalid) throw new OutOfMemoryException();
            return handle;
        }
        public static CFHandle String(string value) => new(CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100));
        public static void Set(CFHandle dictionary, string key, IntPtr value) => CFDictionarySetValue(dictionary.DangerousGetHandle(), Constant(key), value);

        [DllImport(Security)] public static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(Security)] public static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(Security)] public static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
        [DllImport(Security)] public static extern int SecItemDelete(IntPtr query);
        [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr value);
        [DllImport(CoreFoundation)] public static extern nuint CFGetTypeID(IntPtr value);
        [DllImport(CoreFoundation)] public static extern nuint CFDataGetTypeID();
        [DllImport(CoreFoundation)] public static extern nint CFDataGetLength(IntPtr data);
        [DllImport(CoreFoundation)] public static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundation)] public static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
        [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    }
}
