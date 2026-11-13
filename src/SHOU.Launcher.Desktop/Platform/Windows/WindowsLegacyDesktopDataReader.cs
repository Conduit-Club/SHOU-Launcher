using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop.Platform.Windows;

/// <summary>Only the colocated launcher's public settings and the current user's PCLCE Profile are read.</summary>
public sealed class WindowsLegacyDesktopDataReader(string executableDirectory, string sharedDirectory,
    Func<string, CancellationToken, Task<string>>? decryptField = null) : ILegacyDesktopDataReader
{
    public async Task<LegacyDesktopData> ReadAsync(CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>(); var accounts = new List<AuthSession>();
        string? selectedFolder = null, selectedVersion = null, selectedAccount = null;
        var executable = Path.GetFullPath(executableDirectory);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Replace("$", executable + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (!Path.IsPathFullyQualified(path)) path = Path.Combine(executable, path);
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            SafePath.RejectLinks(path);
            if (Directory.Exists(Path.Combine(path, "versions"))) folders.Add(path);
        }
        try
        {
            Add(executable); Add(Path.Combine(executable, ".minecraft"));
            // Match the old launcher's immediate-child discovery, never a whole-drive scan.
            foreach (var child in Directory.EnumerateDirectories(executable).Take(1000))
            {
                ct.ThrowIfCancellationRequested();
                try { Add(child); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add("legacy_child_unreadable"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add("legacy_folder_read:" + ex.GetType().Name); }
        JsonObject? shared = null;
        try
        {
            shared = await ReadJsonAsync(Path.Combine(sharedDirectory, "config.v1.json"), ct);
            if (Text(shared, "LaunchFolders") is { } configured)
                foreach (var part in configured.Split('|').Take(1000)) { var separator = part.IndexOf('>'); if (separator >= 0) Add(part[(separator + 1)..]); }
            // Local v1 YAML is a flat key/value file. Only these two public scalars are needed.
            var local = Path.Combine(executable, "PCL", "config.v1.yml");
            if (File.Exists(local))
            {
                SafePath.RejectLinks(local); if (new FileInfo(local).Length > 2_000_000) throw new InvalidDataException();
                foreach (var line in await File.ReadAllLinesAsync(local, ct))
                {
                    var separator = line.IndexOf(':'); if (separator < 0) continue;
                    var key = line[..separator].Trim();
                    if (key is not ("LaunchFolderSelect" or "LaunchInstanceSelect")) continue;
                    var value = Scalar(line[(separator + 1)..].Trim());
                    if (key == "LaunchFolderSelect") selectedFolder = value;
                    if (key == "LaunchInstanceSelect") selectedVersion = value;
                }
            }
            // Older Setup.ini public keys remain read-only. Do not interpret login/cache fields.
            var ini = Path.Combine(executable, "PCL", "Setup.ini");
            if (File.Exists(ini))
            {
                SafePath.RejectLinks(ini); if (new FileInfo(ini).Length > 2_000_000) throw new InvalidDataException();
                foreach (var line in await File.ReadAllLinesAsync(ini, ct))
                {
                    var colon = line.IndexOf(':'); var equals = line.IndexOf('=');
                    var split = colon < 0 ? equals : equals < 0 ? colon : Math.Min(colon, equals); if (split < 0) continue;
                    var key = line[..split].Trim(); var value = line[(split + 1)..].Trim();
                    if (key == "LaunchFolderSelect") selectedFolder ??= value;
                    if (key == "LaunchInstanceSelect") selectedVersion ??= value;
                    if (key == "LaunchFolders") foreach (var part in value.Split('|').Take(1000)) { var p = part.IndexOf('>'); if (p >= 0) Add(part[(p + 1)..]); }
                }
            }
            Add(selectedFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { issues.Add("legacy_settings_read:" + ex.GetType().Name); }
        if (Text(shared, "Profile") is { Length: > 0 } encoded)
        {
            try
            {
                var decrypt = decryptField ?? new WindowsLegacyCredentialReader(Path.Combine(sharedDirectory, "UserKey.bin")).DecryptFieldAsync;
                var plaintext = await decrypt(encoded, ct);
                if (plaintext.Length > 1_500_000) throw new InvalidDataException();
                var profile = JsonNode.Parse(plaintext) as JsonObject ?? throw new InvalidDataException();
                var rows = profile["profiles"] as JsonArray ?? throw new InvalidDataException();
                if (rows.Count > 128) throw new InvalidDataException();
                var selected = (int?)profile["lastUsed"] ?? -1;
                for (var index = 0; index < rows.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var row = rows[index] as JsonObject ?? throw new InvalidDataException();
                        var name = Required(row, "username", 64); var uuid = Guid.Parse(Required(row, "uuid", 36)).ToString("N");
                        var type = Required(row, "profileType", 32); var provider = Text(row, "provider");
                        var kind = type switch { "Offline" => AccountKind.Offline, "Microsoft" when provider == "microsoft" => AccountKind.Microsoft,
                            "Authlib" => AccountKind.Yggdrasil, _ => throw new InvalidDataException() };
                        var server = kind == AccountKind.Yggdrasil ? AuthenticationClient.NormalizeYggdrasilRoot(new Uri(Required(row, "server", 2048))) : null;
                        var id = kind switch { AccountKind.Microsoft => "microsoft:" + uuid, AccountKind.Yggdrasil => "yggdrasil:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server!)))[..12] + ":" + uuid, _ => "offline:" + uuid };
                        var access = kind == AccountKind.Offline ? "" : Text(row, "accessToken") ?? "";
                        var refresh = kind == AccountKind.Microsoft ? Text(row, "refreshToken") : null;
                        var client = kind == AccountKind.Yggdrasil ? Text(row, "clientToken") : null;
                        if (access.Length > 32768 || refresh?.Length > 32768 || client?.Length > 32768) throw new InvalidDataException();
                        var recoverable = kind == AccountKind.Offline || kind == AccountKind.Microsoft && !string.IsNullOrWhiteSpace(refresh)
                            || kind == AccountKind.Yggdrasil && !string.IsNullOrWhiteSpace(access) && !string.IsNullOrWhiteSpace(client);
                        var account = new AccountProfile(id, kind, name, uuid, server, Status: recoverable ? "旧凭据已接入 · 使用时验证" : "旧凭据不可恢复 · 请重新登录");
                        if (accounts.Any(a => a.Profile.Id == id)) continue;
                        accounts.Add(new(account, recoverable ? access : "", recoverable ? refresh : null, recoverable ? client : null));
                        if (index == selected) selectedAccount = id;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or FormatException or AuthenticationException) { issues.Add("legacy_profile_unsupported"); }
                }
            }
            catch (CredentialStoreException ex) { issues.Add("legacy_credentials:" + ex.Failure); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { issues.Add("legacy_profile_read:" + ex.GetType().Name); }
        }
        string? selectedDirectory = null;
        if (!string.IsNullOrWhiteSpace(selectedFolder) && !string.IsNullOrWhiteSpace(selectedVersion)
            && selectedVersion.IndexOfAny(['/', '\\', ':']) < 0 && selectedVersion is not ("." or ".."))
        {
            var root = selectedFolder.Replace("$", executable + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (Path.IsPathFullyQualified(root)) selectedDirectory = Path.GetFullPath(Path.Combine(root, "versions", selectedVersion));
        }
        return new(folders.ToArray(), accounts, selectedAccount, selectedDirectory, issues);
    }
    private static string Scalar(string value) => value.StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(value) ?? ""
        : value.StartsWith('\'') && value.EndsWith('\'') ? value[1..^1].Replace("''", "'") : value;
    private static string? Text(JsonObject? row, string key) => row?[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    private static string Required(JsonObject row, string key, int limit) => Text(row, key) is { Length: > 0 } text && text.Length <= limit && !text.Any(char.IsControl) ? text : throw new InvalidDataException();
    private static async Task<JsonObject?> ReadJsonAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return null;
        SafePath.RejectLinks(file); if (new FileInfo(file).Length > 2_000_000) throw new InvalidDataException();
        var text = await File.ReadAllTextAsync(file, ct); return JsonNode.Parse(text.TrimStart('\ufeff')) as JsonObject ?? throw new InvalidDataException();
    }
}
