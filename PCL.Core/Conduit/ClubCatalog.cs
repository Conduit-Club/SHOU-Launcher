using System;
using System.Collections.Generic;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Core.Conduit;

public sealed record ClubServer(string Id, string Name, string Address)
{
    public string DisplayName => string.IsNullOrEmpty(Address) ? Name : $"{Name} · {Address}";
}

/// <summary>社团公开文档中的入口；不包含账户凭据。</summary>
public static class ClubCatalog
{
    public const string BrandName = "潮涌核心社";
    public const string BrandNameEnglish = "Conduit Club";
    public const string LauncherName = "潮涌核心社启动器";
    public const string LauncherNameEnglish = "Conduit Club Launcher";
    public const string GameTypeInfo = BrandName;
    public const string LegacyGameTypeInfo = "PCLCE";

    public const string Website = "https://conduit-club.github.io/";
    public const string Repository = "https://github.com/Conduit-Club/PCL-CE-SHOU";
    // MUA Union is a server-side aggregation API. Players authenticate against
    // the member skin site, while the Union endpoint is retained for existing
    // profiles and server compatibility.
    public const string MuaAuth = "https://skin.mualliance.ltd/api/yggdrasil";
    public const string MuaUnionAuth = "https://skin.mualliance.ltd/api/union/yggdrasil";
    public const string LittleSkinAuth = "https://littleskin.cn/api/yggdrasil";
    // The public client ID is configured and the club application has passed
    // Microsoft's review. Actual account login still needs to be verified.
    public const bool MicrosoftApiReady = true;
    public static IReadOnlyList<ClubServer> Servers { get; } = Array.AsReadOnly(new[]
    {
        new ClubServer("none", "只启动游戏", ""),
        new ClubServer("smp", "SMP · 多人生存", "smp.shoumc.com"),
        new ClubServer("create", "Create · 创造建筑", "create.shoumc.com"),
        new ClubServer("shou", "SHOU · 校园展示", "shou.shoumc.com")
    });

    public static ClubServer FindServer(string? id)
    {
        foreach (var server in Servers)
            if (server.Id == id) return server;
        return Servers[0];
    }

    public static string ResolveLaunchServer(string? selectedAddress, string? instanceAddress)
        => selectedAddress ?? instanceAddress ?? string.Empty;

    /// <summary>
    /// Migrate the old default game version type without changing an empty value
    /// or a value chosen by the user.
    /// </summary>
    public static string MigrateLegacyGameTypeInfo(string value)
        => string.Equals(value, LegacyGameTypeInfo, StringComparison.Ordinal)
            ? GameTypeInfo
            : value;

    public static bool CanJoin(McProfile profile) => profile.ProfileType == ProfileType.Microsoft
        || (profile.ProfileType is ProfileType.Authlib or ProfileType.YggdrasilConnect
            && (IsMuaProvider(profile.Server) || IsProvider(profile.Server, LittleSkinAuth)));

    /// <summary>
    /// Recognizes both the current member-site login API and the legacy Union
    /// API used by profiles created before the login endpoint was corrected.
    /// </summary>
    public static bool IsMuaProvider(string? address)
        => IsProvider(address, MuaAuth) || IsProvider(address, MuaUnionAuth);

    public static bool IsProvider(string? address, string expected)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var actual)) return false;
        var target = new Uri(expected);
        var path = actual.AbsolutePath.TrimEnd('/');
        return actual.Scheme == Uri.UriSchemeHttps && actual.Host == target.Host && actual.Port == target.Port
            && (path == target.AbsolutePath || path == target.AbsolutePath + "/authserver");
    }

    public static string AccountSource(McProfile profile) => profile.ProfileType switch
    {
        ProfileType.Microsoft => "微软正版",
        ProfileType.Offline => "离线账户 · 无法进入社团服务器",
        _ when IsMuaProvider(profile.Server) => "MUA Union",
        _ when IsProvider(profile.Server, LittleSkinAuth) => "LittleSkin",
        _ => "第三方 · " + (profile.ServerName ?? profile.Server ?? "自定义认证")
    };

    public static string AccountStatus(McProfile profile)
    {
        if (profile.ProfileType == ProfileType.Offline) return "本地档案";
        if (profile.ProfileType == ProfileType.Authlib &&
            string.IsNullOrWhiteSpace(profile.AccessToken) &&
            !string.IsNullOrWhiteSpace(profile.LoginName) &&
            !string.IsNullOrWhiteSpace(profile.Password))
            return "已导入 · 首次启动时验证";
        if (profile.IsExpired) return "凭据已过期 · 启动时刷新";
        if (string.IsNullOrWhiteSpace(profile.AccessToken)) return "需要重新登录";
        return "已保存 · 启动时验证";
    }
}
