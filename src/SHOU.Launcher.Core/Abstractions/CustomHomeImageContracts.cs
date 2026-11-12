namespace SHOU.Launcher.Core.Abstractions;

public interface ICustomHomeImageService
{
    Task<byte[]> ReadCustomHomeImageAsync(string source, string documentLocation, bool remoteDocument, CancellationToken cancellationToken = default);
}
