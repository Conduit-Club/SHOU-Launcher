using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Link.McPing;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Minecraft;

namespace PCL.Core.Conduit;

/// <summary>社团服务器状态探测结果。</summary>
public sealed record ClubServerStatus(
    string ServerId,
    string Address,
    int? OnlinePlayers,
    int? MaxPlayers,
    long? Latency)
{
    public IReadOnlyList<string> SampleNames { get; init; } = Array.Empty<string>();

    public bool IsKnown => OnlinePlayers.HasValue && MaxPlayers.HasValue;

    public string PlayerSummary => ServerId == "none"
        ? string.Empty
        : IsKnown ? $"在线人数：{OnlinePlayers}/{MaxPlayers}" : "在线人数：未知";

    public static ClubServerStatus Unknown(ClubServer server)
        => new(server.Id, server.Address, null, null, null);

    public static ClubServerStatus FromResult(ClubServer server, McPingResult result)
    {
        var players = result.Players;
        if (players is null || players.Online < 0 || players.Max < 0)
            return Unknown(server);

        var status = new ClubServerStatus(server.Id, server.Address, players.Online, players.Max, result.Latency)
        {
            SampleNames = _GetSampleNames(players.Samples)
        };
        return status;
    }

    private static IReadOnlyList<string> _GetSampleNames(IReadOnlyList<McPingPlayerSampleResult>? samples)
    {
        if (samples is not { Count: > 0 }) return Array.Empty<string>();

        var names = new List<string>();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fallbackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            var name = sample.Name?.Trim() ?? string.Empty;
            var id = _NormalizePlayerId(sample.Id);
            if (id.Length > 0)
            {
                if (ids.TryGetValue(id, out var existingIndex))
                {
                    if (names[existingIndex].Length == 0 && name.Length > 0)
                        names[existingIndex] = name;
                    continue;
                }

                ids[id] = names.Count;
                names.Add(name);
            }
            else if (name.Length > 0 && fallbackNames.Add(name))
            {
                names.Add(name);
            }
        }

        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return names.Where(name => name.Length > 0 && uniqueNames.Add(name)).ToArray();
    }

    private static string _NormalizePlayerId(string? id)
    {
        if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty)
            return string.Empty;
        return guid.ToString("N");
    }
}

/// <summary>
/// 查询社团公开服务器状态。每次查询都按原始主机名创建 Minecraft status ping，
/// 让 Velocity 等 forced-host 配置收到正确的握手主机名。
/// </summary>
public sealed class ClubServerStatusProbe
{
    public const int DefaultPort = 25565;
    public const int DefaultTimeoutMilliseconds = 3000;

    private readonly int _timeoutMilliseconds;
    private readonly Func<string, CancellationToken, Task<ServerAddressResolver.HostPortAddress>> _resolveAddress;
    private readonly Func<string, string?, int, int, IMcPingService> _createPing;

    public ClubServerStatusProbe(
        int timeoutMilliseconds = DefaultTimeoutMilliseconds,
        Func<string, CancellationToken, Task<ServerAddressResolver.HostPortAddress>>? resolveAddress = null,
        Func<string, string?, int, int, IMcPingService>? createPing = null)
    {
        if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        _timeoutMilliseconds = timeoutMilliseconds;
        _resolveAddress = resolveAddress ?? ServerAddressResolver.GetOriginalHostPortAsync;
        _createPing = createPing ?? ((host, ip, port, timeout) =>
            McPingServiceFactory.CreateServiceByHost(host, port, timeout));
    }

    /// <summary>查询单个服务器；无地址的 none 入口不会发起网络请求。</summary>
    public async Task<ClubServerStatus> QueryAsync(ClubServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Id == "none" || string.IsNullOrWhiteSpace(server.Address))
            return ClubServerStatus.Unknown(server);

        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCancellation = new CancellationTokenSource(_timeoutMilliseconds);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCancellation.Token);
        var probeToken = linkedCancellation.Token;
        try
        {
            var resolved = await _resolveAddress(server.Address, probeToken).ConfigureAwait(false);
            probeToken.ThrowIfCancellationRequested();
            // SRV resolution supplies only the port. Keep the original address for both TCP
            // connection and handshake so a proxy can apply its forced-host routing.
            using var ping = _createPing(resolved.Host, null, resolved.Port, _timeoutMilliseconds);
            // The home status card only needs the status response.  Modern proxies may close
            // immediately after status and do not promise a pong; keep the existing full ping
            // behavior for injected/legacy implementations used by other callers and tests.
            var result = ping is McPingService modernPing
                ? await modernPing.PingStatusOnlyAsync(probeToken).ConfigureAwait(false)
                : await ping.PingAsync(probeToken).ConfigureAwait(false);
            probeToken.ThrowIfCancellationRequested();
            return result is null ? ClubServerStatus.Unknown(server) : ClubServerStatus.FromResult(server, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ClubServerStatus.Unknown(server);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A status card should remain usable when one public endpoint is down.
            return ClubServerStatus.Unknown(server);
        }
    }

    /// <summary>并行查询给定服务器，不合并重复主机名，也不查询 none 入口。</summary>
    public async Task<IReadOnlyList<ClubServerStatus>> QueryManyAsync(
        IEnumerable<ClubServer> servers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var targets = servers.Where(server => server is not null &&
                                              server.Id != "none" &&
                                              !string.IsNullOrWhiteSpace(server.Address)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.WhenAll(targets.Select(server => QueryAsync(server, cancellationToken)))
            .ConfigureAwait(false);
    }
}
