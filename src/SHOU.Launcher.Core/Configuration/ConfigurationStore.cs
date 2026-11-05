using System.Text.Json;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Configuration;

public sealed record LauncherState(LauncherSettings Settings, IReadOnlyList<GameInstance> Instances,
    IReadOnlyList<AccountProfile> Accounts, string? SelectedAccountId = null, string? SelectedInstanceId = null);

/// <summary>Versioned preview sidecar; does not rewrite legacy Windows config or encrypted profiles.</summary>
public sealed class ConfigurationStore(IAppPaths paths) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private JsonObject root = new();
    private bool canWrite = true;
    private readonly string file = Core.IO.SafePath.Within(paths.LocalDataDirectory, "shou.desktop.v1.json");
    public bool CanWrite => canWrite;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<LauncherState> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ = Core.IO.SafePath.Within(paths.LocalDataDirectory, "shou.desktop.v1.json");
            if (!File.Exists(file)) return new(new(), [], []);
            try
            {
                root = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
                    ?? throw new InvalidDataException("Configuration root is invalid.");
                if (((int?)root["schemaVersion"] ?? 1) > 1) { canWrite = false; throw new InvalidDataException("Configuration uses a future schema; writing is disabled."); }
                var settings = root["settings"]?.Deserialize<LauncherSettings>(JsonOptions) ?? new();
                var instances = root["instances"]?.Deserialize<GameInstance[]>(JsonOptions) ?? [];
                ValidateInstances(instances, settings);
                return new(settings,
                    instances, root["accounts"]?.Deserialize<AccountProfile[]>(JsonOptions) ?? [],
                    (string?)root["selectedAccountId"], (string?)root["selectedInstanceId"]);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or InvalidOperationException or ArgumentException)
            { canWrite = false; throw new InvalidDataException("Configuration cannot be read safely; original file is preserved and writes are disabled.", ex); }
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(LauncherState state, CancellationToken ct = default, bool writeAccounts = true)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!canWrite) throw new InvalidOperationException("Persistence is disabled after a configuration read failure.");
            ValidateSettings(state.Settings);
            ValidateInstances(state.Instances, state.Settings);
            var next = (JsonObject)root.DeepClone();
            next["schemaVersion"] = 1;
            var settings = root["settings"]?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var property in JsonSerializer.SerializeToNode(state.Settings, JsonOptions)!.AsObject()) settings[property.Key] = property.Value?.DeepClone();
            next["settings"] = settings;
            var oldInstances = (root["instances"] as JsonArray ?? []).OfType<JsonObject>().Where(n => (string?)n["id"] is not null).ToDictionary(n => (string)n["id"]!, StringComparer.Ordinal);
            var mergedInstances = new JsonArray();
            foreach (var instance in state.Instances)
            {
                var node = oldInstances.TryGetValue(instance.Id, out var old) ? (JsonObject)old.DeepClone() : new JsonObject();
                foreach (var property in JsonSerializer.SerializeToNode(instance, JsonOptions)!.AsObject())
                {
                    if (property.Key == "presentation" && property.Value is JsonObject values && node[property.Key] is JsonObject existing)
                    { foreach (var item in values) existing[item.Key] = item.Value?.DeepClone(); }
                    else node[property.Key] = property.Value?.DeepClone();
                }
                mergedInstances.Add(node);
            }
            next["instances"] = mergedInstances;
            if (writeAccounts)
            {
                next["accounts"] = JsonSerializer.SerializeToNode(state.Accounts, JsonOptions);
                next["selectedAccountId"] = state.SelectedAccountId;
            }
            next["selectedInstanceId"] = state.SelectedInstanceId;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            _ = Core.IO.SafePath.Within(paths.LocalDataDirectory, "shou.desktop.v1.json");
            var temporary = Core.IO.SafePath.Within(paths.LocalDataDirectory, "shou.desktop.v1.json.tmp-" + Guid.NewGuid().ToString("N"));
            var created = false;
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    created = true;
                    await JsonSerializer.SerializeAsync(stream, next, JsonOptions, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                File.Move(temporary, file, true);
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
            root = next;
        }
        finally { gate.Release(); }
    }
    public static void ValidateSettings(LauncherSettings settings)
    {
        foreach (var property in typeof(LauncherSettings).GetProperties())
            if (!GeneratedSettingsSchema.Entries.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidOperationException("Unregistered generated settings key: " + property.Name);
        if (settings.MemoryMiB is < 512 or > 32768) throw new ArgumentException("内存须在512–32768 MiB之间。");
        if (settings.MinimumMemoryMiB is { } minimum && (minimum < 512 || minimum > settings.MemoryMiB))
            throw new ArgumentException("初始内存须为512 MiB至最大内存之间的整数。");
        if (settings.GameWindowWidth.HasValue != settings.GameWindowHeight.HasValue ||
            settings.GameWindowWidth is < 320 or > 16384 || settings.GameWindowHeight is < 320 or > 16384)
            throw new ArgumentException("自定义窗口宽高须同时填写，范围为320–16384。");
        if (settings.CustomInfo is { } info && (info.Length > 256 || info.Any(char.IsControl)))
            throw new ArgumentException("自定义信息须为不超过256字的单行文字。");
        if (settings.JavaExecutable is { Length: > 0 } java && !Path.IsPathFullyQualified(java)) throw new ArgumentException("Java路径必须为绝对路径。");
        if (settings.GameRoot is { Length: > 0 } root && !Path.IsPathFullyQualified(root)) throw new ArgumentException("游戏目录必须为绝对路径。");
        if (settings.Theme is not ("System" or "Light" or "Dark")) throw new ArgumentException("未知主题。");
        _ = PCL.Core.Conduit.DownloadSources.Parse(settings.DownloadSourcesJson);
        if (settings.MicrosoftClientId is { Length: > 0 } id && !Guid.TryParse(id, out _)) throw new ArgumentException("Microsoft Client ID须为GUID。");
        if (settings.Proxy is { } declaredProxy && !Enum.IsDefined(declaredProxy.Mode)) throw new ArgumentException("未知代理模式。");
        if (settings.Proxy is { Mode: ProxyMode.Custom } proxy && (!Uri.TryCreate(proxy.Address, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment)))
            throw new ArgumentException("代理须为不含凭据的HTTP(S)地址。");
        if (settings.Aria2 is { Enabled: true } aria && (!Uri.TryCreate(aria.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)))
            throw new ArgumentException("aria2 RPC须为不含凭据的HTTP(S)地址。");
    }
    public static void ValidateInstances(IReadOnlyList<GameInstance> instances, LauncherSettings settings)
    {
        if (instances.Count > 10000 || instances.Any(i => i is null) || instances.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != instances.Count) throw new InvalidDataException("实例数量超限或标识重复。");
        foreach (var instance in instances)
        {
            Core.IO.SafePath.Identifier(instance.Id);
            if (string.IsNullOrWhiteSpace(instance.Name) || instance.Name.Length > 160 || instance.Name.Any(char.IsControl)) throw new InvalidDataException("实例名称无效。");
            if (instance.GameVersionId is { } version) Core.IO.SafePath.Identifier(version);
            if (instance.MemoryMiB is < 512 or > 32768 || settings.MinimumMemoryMiB > (instance.MemoryMiB ?? settings.MemoryMiB)) throw new InvalidDataException("实例内存设置无效。");
            if (instance.Presentation is not { } presentation) continue;
            if (presentation.Icon is null || presentation.Category is null || presentation.Description is null || presentation.LaunchCount < 0 || presentation.Icon.Length > 80 || presentation.Icon.Any(char.IsControl)
                || presentation.Category.Length > 80 || presentation.Category.Any(char.IsControl) || presentation.Description.Length > 4096
                || presentation.Description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new InvalidDataException("实例个性化数据无效。");
        }
    }
    public async ValueTask DisposeAsync() { await gate.WaitAsync().ConfigureAwait(false); gate.Release(); gate.Dispose(); }
}
