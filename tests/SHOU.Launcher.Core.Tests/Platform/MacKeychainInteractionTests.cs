using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.Platform.MacOS;

namespace SHOU.Launcher.Core.Tests.Platform;

[TestClass]
[DoNotParallelize]
[TestCategory("NativeCredential")]
[TestCategory("MacKeychainPolicy")]
public sealed class MacKeychainInteractionTests
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";

    [TestMethod]
    public void NativeInteractionStateIsDisabledInsideScopeAndRestoredAfterFailure()
    {
        RequireMac();
        var previous = State();
        RunGuarded(() => Assert.AreEqual((byte)0, State()));
        Assert.AreEqual(previous, State());
        Assert.Throws<InvalidOperationException>(() => RunGuarded(() =>
        {
            Assert.AreEqual((byte)0, State());
            throw new InvalidOperationException("synthetic failure");
        }));
        Assert.AreEqual(previous, State());
    }

    [TestMethod]
    public async Task NativeInteractionScopeSerializesAllStoreInstances()
    {
        RequireMac();
        var previous = State();
        var active = 0;
        var completed = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunGuarded(() =>
        {
            Assert.AreEqual(1, Interlocked.Increment(ref active));
            try
            {
                Assert.AreEqual((byte)0, State());
                Thread.Sleep(20);
                Interlocked.Increment(ref completed);
            }
            finally { Interlocked.Decrement(ref active); }
        }))));
        Assert.AreEqual(8, completed);
        Assert.AreEqual(previous, State());
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task ForeignCreatorItemFailsReadWithoutInteractionAndCreatorRetainsItsValue()
    {
        RequireMac();
        var fixture = new PlatformFixture();
        var source = fixture.Child("keychain-creator.c");
        var executable = fixture.Child("keychain-creator");
        await File.WriteAllTextAsync(source, CreatorSource);
        await using var runner = new ProcessRunner();
        async Task Run(string command, params string[] args)
        {
            var result = await runner.RunAsync(new ProcessSpec(command, args, fixture.Root,
                Timeout: TimeSpan.FromSeconds(15), OutputLimitCharacters: 4096));
            Assert.AreEqual(0, result.ExitCode, $"Fixture command failed: {Path.GetFileName(command)}; {result.StandardError}");
        }
        await Run("/usr/bin/clang", source, "-o", executable, "-framework", "Security", "-framework", "CoreFoundation");
        await Run("/usr/bin/codesign", "--force", "--sign", "-", "--identifier", "org.shou.task.keychain-creator", executable);
        var service = "org.shou.task.noninteractive." + Guid.NewGuid().ToString("N");
        const string key = "synthetic-only";
        var evidence = Environment.GetEnvironmentVariable("SHOU_KEYCHAIN_POLICY_EVIDENCE") ?? fixture.Child("keychain-policy-evidence.json");
        void Record(string stage, string? failure = null, double? elapsedSeconds = null) => File.WriteAllText(evidence,
            JsonSerializer.Serialize(new { Stage = stage, Creator = executable, Service = service, Key = key,
                Failure = failure, ElapsedSeconds = elapsedSeconds, RealCredentialsUsed = false }));
        var previous = State();
        Record("CREATOR_PREPARED");
        await Run(executable, "write", service, key);
        Record("CREATOR_WROTE");
        string? denied = null;
        double? elapsed = null;
        try
        {
            var watch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<CredentialStoreException>(() => new KeychainCredentialStore(service).ReadAsync(key));
            denied = error.Message;
            elapsed = watch.Elapsed.TotalSeconds;
            Record("READ_REJECTED", denied, elapsed);
            Assert.IsTrue(error.Failure is CredentialFailure.Locked or CredentialFailure.Denied, error.Message);
            Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed, "An inaccessible item must fail without waiting for UI.");
            Assert.AreEqual(previous, State());
            // Only the original creator checks the synthetic bytes and removes its item. No ACL API is used.
            await Run(executable, "read", service, key);
            Record("CREATOR_VALUE_PRESERVED", denied, elapsed);
        }
        finally { await Run(executable, "delete", service, key); Record("CREATOR_CLEANED", denied, elapsed); }
    }

    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("Native macOS Keychain policy evidence requires macOS.");
    }

    private static byte State()
    {
        Assert.AreEqual(0, SecKeychainGetUserInteractionAllowed(out var value));
        return value;
    }

    private static void RunGuarded(Action action)
    {
        var method = typeof(KeychainCredentialStore).GetMethod("WithoutInteraction", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int));
        try { method.Invoke(null, new object[] { new Func<int>(() => { action(); return 0; }) }); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    [DllImport(Security)] private static extern int SecKeychainGetUserInteractionAllowed(out byte state);

    private const string CreatorSource = """
        #include <Security/Security.h>
        #include <CoreFoundation/CoreFoundation.h>
        #include <stdio.h>
        #include <string.h>
        int main(int argc, char **argv) {
            if (argc != 4) return 2;
            OSStatus s = SecKeychainSetUserInteractionAllowed(false);
            if (s) { fprintf(stderr, "interaction OSStatus %d\n", (int)s); return 3; }
            CFStringRef service = CFStringCreateWithCString(NULL, argv[2], kCFStringEncodingUTF8);
            CFStringRef account = CFStringCreateWithCString(NULL, argv[3], kCFStringEncodingUTF8);
            CFMutableDictionaryRef q = CFDictionaryCreateMutable(NULL, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
            CFDictionarySetValue(q, kSecClass, kSecClassGenericPassword);
            CFDictionarySetValue(q, kSecAttrService, service);
            CFDictionarySetValue(q, kSecAttrAccount, account);
            CFDictionarySetValue(q, kSecUseAuthenticationUI, kSecUseAuthenticationUIFail);
            const UInt8 fake[] = "synthetic-private-fixture-v1";
            CFDataRef expected = CFDataCreate(NULL, fake, sizeof(fake) - 1);
            if (!strcmp(argv[1], "write")) {
                CFDictionarySetValue(q, kSecValueData, expected);
                s = SecItemAdd(q, NULL);
            } else if (!strcmp(argv[1], "read")) {
                CFDictionarySetValue(q, kSecReturnData, kCFBooleanTrue);
                CFDictionarySetValue(q, kSecMatchLimit, kSecMatchLimitOne);
                CFTypeRef value = NULL;
                s = SecItemCopyMatching(q, &value);
                if (!s && (!value || !CFEqual(value, expected))) s = errSecDecode;
                if (value) CFRelease(value);
            } else if (!strcmp(argv[1], "delete")) {
                s = SecItemDelete(q);
            } else s = errSecParam;
            CFRelease(expected); CFRelease(q); CFRelease(service); CFRelease(account);
            printf("creator %s OSStatus %d\n", argv[1], (int)s);
            return s == errSecSuccess ? 0 : 1;
        }
        """;
}
