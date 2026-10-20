using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using PCL.Core.Link.McPing;
using PCL.Core.Link.McPing.Model;
using PCL.Core.Minecraft;

namespace PCL.Core.Test.Conduit;

[TestClass]
public class ClubServerStatusProbeTest
{
    [TestMethod]
    public async Task QueryManySkipsNoneAndKeepsOriginalHandshakeHosts()
    {
        var resolvedHosts = new ConcurrentBag<string>();
        var pingHosts = new ConcurrentBag<string>();
        var pingIps = new ConcurrentBag<string>();
        var probe = new ClubServerStatusProbe(
            resolveAddress: (host, _) =>
            {
                resolvedHosts.Add(host);
                return Task.FromResult(new ServerAddressResolver.HostPortAddress(
                    host, 25565));
            },
            createPing: (host, ip, _, _) =>
            {
                pingHosts.Add(host);
                pingIps.Add(ip ?? "<none>");
                return new FakePingService(new McPingResult(
                    new McPingVersionResult("1.21.1", 772),
                    new McPingPlayerResult(5, 7, []),
                    string.Empty, null, 18, null, null));
            });

        var statuses = await probe.QueryManyAsync(new[]
        {
            new ClubServer("none", "只启动游戏", string.Empty),
            new ClubServer("smp", "SMP", "smp.shoumc.com"),
            new ClubServer("create", "Create", "create.shoumc.com")
        });

        Assert.AreEqual(2, statuses.Count);
        CollectionAssert.AreEquivalent(new[] { "smp.shoumc.com", "create.shoumc.com" }, resolvedHosts.ToArray());
        CollectionAssert.AreEquivalent(new[] { "smp.shoumc.com", "create.shoumc.com" }, pingHosts.ToArray());
        CollectionAssert.AreEquivalent(new[] { "<none>", "<none>" }, pingIps.ToArray());
        var smp = statuses.Single(status => status.ServerId == "smp");
        Assert.IsTrue(smp.IsKnown);
        Assert.AreEqual(7, smp.OnlinePlayers);
        Assert.AreEqual(5, smp.MaxPlayers);
    }

    [TestMethod]
    public async Task FailedResponseIsUnknownAndDoesNotBecomeZero()
    {
        var probe = new ClubServerStatusProbe(
            resolveAddress: (host, _) => Task.FromResult(
                new ServerAddressResolver.HostPortAddress(host, 25565)),
            createPing: (_, _, _, _) => new FakePingService(null));

        var status = await probe.QueryAsync(new ClubServer("smp", "SMP", "smp.shoumc.com"));

        Assert.IsFalse(status.IsKnown);
        Assert.IsNull(status.OnlinePlayers);
        Assert.IsNull(status.MaxPlayers);
        Assert.AreEqual("在线人数：未知", status.PlayerSummary);
    }

    [TestMethod]
    public async Task StatusProbeDoesNotNeedResolvedIp()
    {
        string? passedIp = "sentinel";
        string? passedHost = null;
        var passedPort = 0;
        var probe = new ClubServerStatusProbe(
            resolveAddress: (host, _) => Task.FromResult(
                new ServerAddressResolver.HostPortAddress(host, 25565)),
            createPing: (host, ip, port, _) =>
            {
                passedHost = host;
                passedIp = ip;
                passedPort = port;
                return new FakePingService(null);
            });

        var status = await probe.QueryAsync(new ClubServer("smp", "SMP", "smp.shoumc.com"));

        Assert.IsFalse(status.IsKnown);
        Assert.AreEqual("smp.shoumc.com", passedHost);
        Assert.IsNull(passedIp);
        Assert.AreEqual(25565, passedPort);
    }

    [TestMethod]
    public void StatusSamplesPreferUuidAndUseNameAsFallback()
    {
        var server = new ClubServer("smp", "SMP", "smp.shoumc.com");
        var result = new McPingResult(
            new McPingVersionResult("1.21.1", 767),
            new McPingPlayerResult(40, 2, new List<McPingPlayerSampleResult>
            {
                new("FirstName", "12345678-1234-1234-1234-1234567890AB"),
                new("RenamedBot", "123456781234123412341234567890AB"),
                new("FallbackName", string.Empty),
                new("fallbackname", string.Empty),
                new("ZeroNameA", "00000000-0000-0000-0000-000000000000"),
                new("ZeroNameB", "00000000000000000000000000000000")
            }),
            string.Empty, null, 12, null, null);

        var status = ClubServerStatus.FromResult(server, result);

        CollectionAssert.AreEqual(new[] { "FirstName", "FallbackName", "ZeroNameA", "ZeroNameB" }, status.SampleNames.ToArray());
    }

    [TestMethod]
    public async Task OverallTimeoutReturnsUnknownButCallerCancellationPropagates()
    {
        static async Task<ServerAddressResolver.HostPortAddress> WaitForCancellation(
            string host, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ServerAddressResolver.HostPortAddress(host, 25565);
        }

        var probe = new ClubServerStatusProbe(timeoutMilliseconds: 20, resolveAddress: WaitForCancellation);
        var server = new ClubServer("smp", "SMP", "smp.shoumc.com");

        var timedOut = await probe.QueryAsync(server);
        Assert.IsFalse(timedOut.IsKnown);
        Assert.IsNull(timedOut.OnlinePlayers);

        using var callerCancellation = new CancellationTokenSource();
        var pending = probe.QueryAsync(server, callerCancellation.Token);
        callerCancellation.Cancel();
        var callerCancellationPropagated = false;
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
            callerCancellationPropagated = true;
        }
        Assert.IsTrue(callerCancellationPropagated);
    }

    private sealed class FakePingService(McPingResult? result) : IMcPingService
    {
        public IPEndPoint Endpoint { get; } = new(IPAddress.Loopback, 25565);
        public string Host => "test";
        public int Timeout => 1000;

        public Task<McPingResult?> PingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public void Dispose()
        {
        }
    }
}
