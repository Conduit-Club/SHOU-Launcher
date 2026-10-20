using System;
using System.Collections.Generic;
using System.Net.Http;

namespace PCL.Core.Minecraft.IdentityModel.OAuth;

public record OAuthClientOptions
{
    /// <summary>
    /// 请求头
    /// </summary>
    public Dictionary<string,string>? Headers { get; set; }
    /// <summary>
    /// 端点数据
    /// </summary>
    public required EndpointMeta Meta { get; set; }
    public required Func<HttpClient> GetClient { get; set; }
    /// <summary>
    /// 是否附加启动器默认的请求元数据。测试客户端可关闭此项以隔离应用元数据资源。
    /// </summary>
    public bool AddRequestMetadata { get; set; } = true;
    /// <summary>
    /// 重定向 Uri
    /// </summary>
    public required string RedirectUri { get; set; }
    /// <summary>
    /// 客户端 ID
    /// </summary>
    public required string ClientId { get; set; }
}
