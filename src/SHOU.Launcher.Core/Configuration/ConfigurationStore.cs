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
    private readonly string file = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json");
    public bool CanWrite => canWrite;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<LauncherState> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(file)) return new(new(), [], []);
            try
            {
                root = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
                    ?? throw new InvalidDataException("Configuration root is invalid.");
                if (((int?)root["schemaVersion"] ?? 1) > 1) { canWrite = false; throw new InvalidDataException("Configuration uses a future schema; writing is disabled."); }
                return new(root["settings"]?.Deserialize<LauncherSettings>(JsonOptions) ?? new(),
                    root["instances"]?.Deserialize<GameInstance[]>(JsonOptions) ?? [], root["accounts"]?.Deserialize<AccountProfile[]>(JsonOptions) ?? [],
                    (string?)root["selectedAccountId"], (string?)root["selectedInstanceId"]);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or InvalidOperationException)
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
            var next = (JsonObject)root.DeepClone();
            next["schemaVersion"] = 1;
            var settings = root["settings"]?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var property in JsonSerializer.SerializeToNode(state.Settings, JsonOptions)!.AsObject()) settings[property.Key] = property.Value?.DeepClone();
            next["settings"] = settings;
            next["instances"] = JsonSerializer.SerializeToNode(state.Instances, JsonOptions);
            if (writeAccounts)
            {
                next["accounts"] = JsonSerializer.SerializeToNode(state.Accounts, JsonOptions);
                next["selectedAccountId"] = state.SelectedAccountId;
            }
            next["selectedInstanceId"] = state.SelectedInstanceId;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file + ".tmp", next.ToJsonString(JsonOptions), ct).ConfigureAwait(false);
            File.Move(file + ".tmp", file, true);
            root = next;
        }
        finally { gate.Release(); }
    }
    public static void ValidateSettings(LauncherSettings settings)
    {
        foreach (var property in typeof(LauncherSettings).GetProperties())
            if (!GeneratedSettingsSchema.Entries.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidOperationException("Unregistered generated settings key: " + property.Name);
        if (settings.MemoryMiB is < 512 or > 32768) throw new ArgumentException("内存须在512–32768 MiB之间。");
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
    public async ValueTask DisposeAsync() { await gate.WaitAsync().ConfigureAwait(false); gate.Release(); gate.Dispose(); }
}
