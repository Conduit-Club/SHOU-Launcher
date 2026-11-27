using System.Text.Json.Nodes;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record MinecraftServerEndpoint(string Host, int Port, string Address);
public static class ServerLaunchPolicy
{
    public static MinecraftServerEndpoint Parse(string address)
    {
        address = address.Trim();
        if (address.Length is < 1 or > 255 || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl)
            || address.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0 || address.Count(c => c == ':') > 1 && !address.StartsWith('[')
            || !Uri.TryCreate("minecraft://" + address, UriKind.Absolute, out var uri)
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new ArgumentException("服务器地址无效；请填写主机名或 IP，可附加端口。IPv6 请使用方括号。");
        var port = uri.Port < 0 ? 25565 : uri.Port;
        if (port is < 1 or > 65535) throw new ArgumentException("服务器端口须为 1–65535。");
        var host = uri.IdnHost.Trim('[', ']');
        var visibleHost = uri.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host;
        return new(host, port, visibleHost + (port == 25565 ? "" : ":" + port));
    }
    public static bool SupportsQuickPlay(JsonObject metadata)
        => metadata["arguments"]?["game"] is JsonArray args && args.ToJsonString().Contains("${quickPlayMultiplayer}", StringComparison.Ordinal);

    public static LaunchInputs Configure(LaunchInputs input)
    {
        if (input.ServerAddress is null) return input;
        var endpoint = Parse(input.ServerAddress);
        if (input.ExtraGameArguments?.Any(a => new[] { "--server", "--port", "--quickPlayMultiplayer", "--quickPlaySingleplayer", "--quickPlayRealms" }.Any(flag => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal))) == true)
            throw new ArgumentException("已选择快捷进服，请先清空高级游戏参数中的其它服务器或世界目标。");
        var features = new Dictionary<string, bool>(input.Context.Features ?? new Dictionary<string, bool>())
        { ["is_quick_play_multiplayer"] = SupportsQuickPlay(input.Version.Metadata), ["is_quick_play_singleplayer"] = false,
          ["is_quick_play_realms"] = false, ["has_quick_plays_support"] = SupportsQuickPlay(input.Version.Metadata) };
        var extras = input.ExtraGameArguments?.ToList() ?? [];
        if (!SupportsQuickPlay(input.Version.Metadata)) { extras.AddRange(["--server", endpoint.Host, "--port", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]); }
        return input with { ServerAddress = endpoint.Address, Context = input.Context with { Features = features }, ExtraGameArguments = extras };
    }
}
