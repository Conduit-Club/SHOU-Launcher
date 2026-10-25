using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Link.Lobby;

/// <summary>Local peer selection; no remote relay list or account service is required.</summary>
public static class LobbyRelayPolicy
{
    public static string PeerId { get; } = Guid.NewGuid().ToString("N");

    public static IReadOnlyList<string> GetPeers(string customPeers, bool customOnly)
    {
        var custom = customPeers.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var peer in custom)
            if (!Uri.TryCreate(peer, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("tcp" or "udp" or "ws" or "wss" or "quic")
                || string.IsNullOrEmpty(uri.Host))
                throw new ArgumentException($"无效的 EasyTier 节点地址：{peer}");
        if (customOnly && custom.Length == 0)
            throw new ArgumentException("仅使用自定义节点时，请至少填写一个节点地址。");
        return custom.Concat(customOnly ? [] : new[]
            { "tcp://public.easytier.top:11010", "tcp://public2.easytier.cn:54321" })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
