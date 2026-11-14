using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    private async Task<LauncherState> ImportFirstRunLegacyDataAsync(LauncherState state, CancellationToken ct)
    {
        // A committed sidecar is also the migration receipt: removed accounts/instances never reappear.
        if (platform.LegacyDesktopData is null || File.Exists(Path.Combine(Paths.LocalDataDirectory, "shou.desktop.v1.json"))) return state;
        var legacy = await platform.LegacyDesktopData.ReadAsync(ct).ConfigureAwait(false);
        foreach (var issue in legacy.Issues) Log("Warning", "旧数据接入：" + issue);
        var settings = LegacyLaunchSettings.MapGlobalRuntimeSettings(legacy.LaunchSettings, state.Settings,
            legacy.ExecutableDirectory, out var runtimeNotices);
        foreach (var notice in runtimeNotices) Log("Warning", notice);
        var instances = state.Instances.ToList();
        foreach (var folder in legacy.GameFolders)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var inspection = await ExistingInstanceReader.InspectDetailedAsync(folder, ct).ConfigureAwait(false);
                foreach (var candidate in inspection.Candidates)
                {
                    if (instances.Any(i => i.Origin is { } origin && PathEquals(origin.VersionDirectory, candidate.Origin.VersionDirectory))) continue;
                    var instance = new GameInstance(Guid.NewGuid().ToString("N"), candidate.Name, candidate.VersionId, candidate.GameDirectory,
                        Loader: candidate.Loader, LoaderVersion: candidate.LoaderVersion, Installed: true, GameVersionId: candidate.GameVersion,
                        Origin: candidate.Origin, Presentation: candidate.Presentation);
                    instances.Add(await ImportLegacyRuntimeSettingsAsync(instance, settings, legacy.ExecutableDirectory, ct).ConfigureAwait(false));
                }
                if (inspection.Issues.Count > 0) Log("Warning", "部分旧实例元数据无法核实；已保留原目录，未接入这些实例。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { Log("Warning", "旧实例目录暂不可读；原文件保留。原因类型：" + ex.GetType().Name); }
        }
        var accounts = state.Accounts.ToList(); var createdKeys = new List<string>();
        try
        {
            foreach (var original in legacy.Accounts)
            {
                ct.ThrowIfCancellationRequested(); var profile = original.Profile;
                if (!AccountIdentity.Matches(profile, profile) || accounts.Any(a => a.Id == profile.Id)) continue;
                if (profile.Kind != AccountKind.Offline && (!string.IsNullOrEmpty(original.RefreshToken) || !string.IsNullOrEmpty(original.AccessToken)))
                {
                    try
                    {
                        var key = AccountKey(profile.Id);
                        var existing = await platform.Credentials.ReadAsync(key, ct).ConfigureAwait(false);
                        if (existing is not null)
                        {
                            try
                            {
                                var stored = JsonSerializer.Deserialize<AuthSession>(existing, ConfigurationStore.JsonOptions);
                                if (stored is null || !AccountIdentity.Matches(profile, stored.Profile)) throw new InvalidDataException("Protected legacy identity mismatch.");
                            }
                            finally { CryptographicOperations.ZeroMemory(existing); }
                        }
                        else
                        {
                            var bytes = JsonSerializer.SerializeToUtf8Bytes(original, ConfigurationStore.JsonOptions);
                            try { await platform.Credentials.WriteAsync(key, bytes, ct).ConfigureAwait(false); createdKeys.Add(key); }
                            finally { CryptographicOperations.ZeroMemory(bytes); }
                        }
                        profile = profile with { Persisted = true };
                    }
                    catch (CredentialStoreException ex)
                    {
                        profile = profile with { Persisted = false, Status = "旧凭据未能安全保存 · 请重新登录" };
                        Log("Warning", "旧凭据安全存储不可用；保留原文件与账户记录。原因类型：" + ex.Failure);
                    }
                }
                accounts.Add(profile);
            }
            if (instances.Count == state.Instances.Count && accounts.Count == state.Accounts.Count && settings == state.Settings) return state;
            var next = state with { Settings = settings, Instances = instances, Accounts = accounts,
                SelectedAccountId = accounts.Any(a => a.Id == legacy.SelectedAccountId) ? legacy.SelectedAccountId : accounts.FirstOrDefault()?.Id,
                SelectedInstanceId = instances.FirstOrDefault(i => i.Origin is { } o && legacy.SelectedVersionDirectory is { } selected && PathEquals(o.VersionDirectory, selected))?.Id ?? instances.FirstOrDefault()?.Id };
            await config.SaveAsync(next, ct).ConfigureAwait(false);
            Log("Info", $"已接入旧实例 {instances.Count} 个、账户 {accounts.Count} 个；原文件保留，在线凭据仍须服务端验证。");
            return next;
        }
        catch
        {
            // Only records created by this transaction can be rolled back. Never touch an existing record.
            foreach (var key in createdKeys)
                try { await platform.Credentials.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or CredentialStoreException) { Log("Warning", "未完成迁移的安全凭据清理失败：" + ex.GetType().Name); }
            throw;
        }
    }
}
