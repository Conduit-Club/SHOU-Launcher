using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Desktop;

public static partial class Bootstrap
{
    private static async Task<int?> RunCommandAsync()
    {
        var launcher = Launcher ?? throw new InvalidOperationException("Launcher is not initialized.");
        var timeoutSeconds = int.TryParse(ArgumentValue("--exit-after"), out var value) ? value : 1800;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var ct = deadline.Token;
        if (Arguments.Contains("--diagnostics", StringComparer.Ordinal))
        {
            var java = await launcher.DiscoverJavaAsync(ct).ConfigureAwait(false);
            WriteCommandReport(new { status = "PASS", scope = "diagnostics_only", build = launcher.Snapshot.Build,
                platform = launcher.Snapshot.Platform, runtime = RuntimeInformation.FrameworkDescription,
                paths = new { launcher.Paths.DataDirectory, launcher.Paths.SharedDataDirectory, launcher.Paths.LocalDataDirectory,
                    launcher.Paths.CacheDirectory, launcher.Paths.LogsDirectory, launcher.Paths.RuntimesDirectory,
                    launcher.Paths.InstancesDirectory, launcher.Paths.TempDirectory, launcher.Paths.ResourcesDirectory,
                    launcher.Paths.LockFile, launcher.Paths.IsIsolated },
                configurationAssembly = typeof(BuildConfiguration).Assembly.Location, dataLockAcquired = dataLease is not null,
                java, generatedSettings = GeneratedSettingsSchema.Entries, lifecycle = GeneratedLifecycle.Entries,
                capabilities = new { launcher.Capabilities.SupportsCredentialPersistence, launcher.Capabilities.SupportsSystemProxy, launcher.Capabilities.Limitations } });
            return 0;
        }
        if (ArgumentValue("--credential-smoke") is { } credentialAction)
        {
            var run = Core.IO.SafePath.Identifier(ArgumentValue("--run-id") ?? throw new ArgumentException("Credential smoke requires run-id."));
            var key = "native-smoke:" + run;
            var store = platformServices!.Credentials;
            var expected = Encoding.UTF8.GetBytes("SHOU isolated native credential fixture v1");
            if (credentialAction == "write") await store.WriteAsync(key, expected, ct).ConfigureAwait(false);
            else if (credentialAction == "delete") await store.DeleteAsync(key, ct).ConfigureAwait(false);
            else if (credentialAction != "read") throw new ArgumentException("Unknown credential smoke action.");
            var actual = await store.ReadAsync(key, ct).ConfigureAwait(false);
            var matched = credentialAction == "delete" ? actual is null : actual is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
            if (actual is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(actual);
            WriteCommandReport(new { status = matched ? "PASS" : "FAIL", scope = "isolated_native_credential", action = credentialAction,
                matched, launcher.Paths.CredentialScope, launcher.Paths.IsIsolated, sourceSha = launcher.Snapshot.Build.SourceSha });
            return matched ? 0 : 1;
        }
        if (ArgumentValue("--dry-run-launch") is { } selected)
        {
            var plan = await launcher.BuildLaunchPlanAsync(selected, ct).ConfigureAwait(false);
            WriteCommandReport(new { status = "PASS", scope = "launch_plan_only", plan = plan.ToRedactedReport() }); return 0;
        }
        var prepare = Arguments.Contains("--prepare-minecraft", StringComparer.Ordinal);
        var launch = Arguments.Contains("--launch-minecraft", StringComparer.Ordinal);
        if (!prepare && !launch) return null;
        var settings = launcher.Snapshot.Settings;
        if (ArgumentValue("--java-path") is { } javaPath) settings = settings with { JavaExecutable = javaPath };
        if (Arguments.Contains("--official-downloads", StringComparer.Ordinal))
            settings = settings with { DownloadSourcesJson = JsonSerializer.Serialize(DownloadSources.Defaults().Where(source => source.Layout == SourceLayout.Official)) };
        await launcher.SaveSettingsAsync(settings, ct).ConfigureAwait(false);
        var profileName = ArgumentValue("--offline-profile") ?? "SHOUNightTest";
        var account = launcher.Snapshot.Accounts.FirstOrDefault(a => a.Kind == AccountKind.Offline && a.DisplayName == profileName)
            ?? await launcher.AddOfflineAccountAsync(profileName, ct).ConfigureAwait(false);
        await launcher.SelectAccountAsync(account.Id, ct).ConfigureAwait(false);
        var version = ArgumentValue("--version") ?? "1.21.1";
        var instance = launcher.Snapshot.Instances.FirstOrDefault(i => i.VersionId == version && i.Loader == "vanilla")
            ?? await launcher.CreateInstanceAsync("Minecraft " + version + " · 隔离验证", version, ct).ConfigureAwait(false);
        await launcher.SelectInstanceAsync(instance.Id, ct).ConfigureAwait(false);
        if (launch)
        {
            await launcher.LaunchAsync(instance.Id, "none", ct).ConfigureAwait(false);
            try { while (launcher.Snapshot.GameProcessId is not null) await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { await launcher.StopGameAsync().ConfigureAwait(false); throw; }
            var success = launcher.Snapshot.State != "Error";
            WriteCommandReport(new { status = success ? "PASS" : "FAIL", scope = "process_lifecycle_only", instanceId = instance.Id,
                version, offlineProfile = true, serverJoined = false, sourceSha = launcher.Snapshot.Build.SourceSha,
                finalState = launcher.Snapshot.State, evidenceBoundary = "Minecraft main-menu evidence must be observed separately through native GUI" });
            return success ? 0 : 1;
        }
        await launcher.InstallInstanceAsync(instance.Id, ct).ConfigureAwait(false);
        var dryRun = await launcher.BuildLaunchPlanAsync(instance.Id, ct).ConfigureAwait(false);
        WriteCommandReport(new { status = "PASS", scope = "verified_install_and_plan_only", instanceId = instance.Id,
            version, offlineProfile = true, serverJoined = false, sourceSha = launcher.Snapshot.Build.SourceSha,
            plan = dryRun.ToRedactedReport(), evidenceBoundary = "No game was started; this is not L4 evidence" });
        return 0;
    }
}
