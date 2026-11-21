using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform.MacOS;

/// <summary>Security.framework verifies live processes, not a caller-supplied path or PID alone.</summary>
internal static class MacCodeIdentity
{
    internal sealed record Identity(string Identifier, string? CertificateSha1, uint Flags);
    internal static Identity ReadSelf()
    {
        Check(Native.SecCodeCopySelf(0, out var pointer));
        using var code = new Handle(pointer);
        return Read(code.DangerousGetHandle());
    }

    internal static void VerifyChild(int pid, string identifier, string certificateSha1)
    {
        using var attributes = new Handle(Native.CFDictionaryCreateMutable(0, 0,
            NativeLibrary.GetExport(Native.Foundation, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(Native.Foundation, "kCFTypeDictionaryValueCallBacks")));
        using var number = new Handle(Native.CFNumberCreate(0, 3, ref pid)); // kCFNumberSInt32Type
        Native.CFDictionarySetValue(attributes.DangerousGetHandle(), Native.Constant("kSecGuestAttributePid"), number.DangerousGetHandle());
        Check(Native.SecCodeCopyGuestWithAttributes(0, attributes.DangerousGetHandle(), 0, out var pointer));
        using var code = new Handle(pointer);
        using var expression = String($"identifier \"{identifier}\" and certificate leaf = H\"{certificateSha1}\"");
        Check(Native.SecRequirementCreateWithString(expression.DangerousGetHandle(), 0, out var requirementPointer));
        using var requirement = new Handle(requirementPointer);
        Check(Native.SecCodeCheckValidity(code.DangerousGetHandle(), 0, requirement.DangerousGetHandle()));
        var identity = Read(code.DangerousGetHandle());
        // CS_RUNTIME: the broker has no JIT, plug-in or DYLD exception entitlements.
        if (identity.Identifier != identifier || identity.CertificateSha1 != certificateSha1 || (identity.Flags & 0x10000) == 0)
            throw new CredentialStoreException(CredentialFailure.Denied, "凭据组件的签名身份或运行保护无效。");
    }

    private static Identity Read(nint code)
    {
        Check(Native.SecCodeCopySigningInformation(code, 2, out var pointer)); // kSecCSSigningInformation
        using var info = new Handle(pointer);
        var identifier = Native.CFDictionaryGetValue(pointer, Native.Constant("kSecCodeInfoIdentifier"));
        var text = new StringBuilder(2048);
        if (identifier == 0 || !Native.CFStringGetCString(identifier, text, text.Capacity, 0x08000100))
            throw new CredentialStoreException(CredentialFailure.Unavailable, "无法核实本机程序签名。");
        var flagsPointer = Native.CFDictionaryGetValue(pointer, Native.Constant("kSecCodeInfoFlags"));
        var flags = 0;
        if (flagsPointer != 0) Native.CFNumberGetValue(flagsPointer, 3, out flags);
        string? fingerprint = null;
        var certificates = Native.CFDictionaryGetValue(pointer, Native.Constant("kSecCodeInfoCertificates"));
        if (certificates != 0 && Native.CFArrayGetCount(certificates) > 0)
        {
            using var data = new Handle(Native.SecCertificateCopyData(Native.CFArrayGetValueAtIndex(certificates, 0)));
            var length = data.IsInvalid ? 0 : Native.CFDataGetLength(data.DangerousGetHandle());
            if (length is <= 0 or > 65536) throw new CredentialStoreException(CredentialFailure.Corrupt, "程序签名证书无效。");
            var bytes = new byte[(int)length];
            Marshal.Copy(Native.CFDataGetBytePtr(data.DangerousGetHandle()), bytes, 0, bytes.Length);
            // The certificate fingerprint syntax is defined by Apple's requirement language.
            fingerprint = Convert.ToHexString(SHA1.HashData(bytes));
        }
        return new(text.ToString(), fingerprint, unchecked((uint)flags));
    }

    private static Handle String(string value) => new(Native.CFStringCreateWithCString(0, value, 0x08000100));
    private static void Check(int status)
    {
        if (status != 0) throw new CredentialStoreException(CredentialFailure.Denied, "本机程序签名校验未通过，未发送登录凭据。");
    }
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal Handle(nint pointer) : base(true) { SetHandle(pointer); }
        protected override bool ReleaseHandle() { Native.CFRelease(handle); return true; }
    }

    private static class Native
    {
        private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
        private const string FoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly nint Security = NativeLibrary.Load(SecurityPath);
        internal static readonly nint Foundation = NativeLibrary.Load(FoundationPath);
        internal static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Security, name));
        [DllImport(SecurityPath)] internal static extern int SecCodeCopySelf(uint flags, out nint code);
        [DllImport(SecurityPath)] internal static extern int SecCodeCopySigningInformation(nint code, uint flags, out nint info);
        [DllImport(SecurityPath)] internal static extern int SecCodeCopyGuestWithAttributes(nint host, nint attributes, uint flags, out nint code);
        [DllImport(SecurityPath)] internal static extern int SecRequirementCreateWithString(nint expression, uint flags, out nint requirement);
        [DllImport(SecurityPath)] internal static extern int SecCodeCheckValidity(nint code, uint flags, nint requirement);
        [DllImport(SecurityPath)] internal static extern nint SecCertificateCopyData(nint certificate);
        [DllImport(FoundationPath)] internal static extern void CFRelease(nint value);
        [DllImport(FoundationPath)] internal static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
        [DllImport(FoundationPath)] internal static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
        [DllImport(FoundationPath)] internal static extern nint CFDictionaryGetValue(nint dictionary, nint key);
        [DllImport(FoundationPath)] internal static extern nint CFNumberCreate(nint allocator, int type, ref int value);
        [DllImport(FoundationPath)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFNumberGetValue(nint number, int type, out int value);
        [DllImport(FoundationPath)] internal static extern nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
        [DllImport(FoundationPath)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFStringGetCString(nint text, StringBuilder buffer, nint size, uint encoding);
        [DllImport(FoundationPath)] internal static extern nint CFArrayGetCount(nint array);
        [DllImport(FoundationPath)] internal static extern nint CFArrayGetValueAtIndex(nint array, nint index);
        [DllImport(FoundationPath)] internal static extern nint CFDataGetLength(nint data);
        [DllImport(FoundationPath)] internal static extern nint CFDataGetBytePtr(nint data);
    }
}
