using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ICustomHomeImageService
{
    public async Task<byte[]> ReadCustomHomeImageAsync(string source, string documentLocation, bool remoteDocument, CancellationToken cancellationToken = default)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.IsLoopback) throw new ArgumentException("网络主页图片须为不含凭据的 HTTPS 地址。");
            return await ReadPublicDocumentAsync(uri, 20 * 1024 * 1024, cancellationToken, "image/*");
        }
        if (remoteDocument)
        {
            if (!Uri.TryCreate(documentLocation, UriKind.Absolute, out var document) || !Uri.TryCreate(document, source, out var resolved) || resolved.Scheme != "https" || resolved.UserInfo.Length != 0 || resolved.IsLoopback) throw new ArgumentException("网络主页不能读取本地图片。");
            return await ReadPublicDocumentAsync(resolved, 20 * 1024 * 1024, cancellationToken, "image/*");
        }
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(documentLocation)) throw new ArgumentException("图片路径无效。");
        var path = Path.IsPathFullyQualified(source) ? Path.GetFullPath(source) : Path.GetFullPath(source, Path.GetDirectoryName(documentLocation)!);
        SafePath.RejectLinks(path); if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new IOException("主页图片过大。");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }
}
