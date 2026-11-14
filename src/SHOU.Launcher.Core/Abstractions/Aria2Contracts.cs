namespace SHOU.Launcher.Core.Abstractions;

public sealed record Aria2ConnectionStatus(bool Connected, string Message, string Endpoint, string? Version = null);
public interface IAria2ConnectionService
{
    Task<Aria2ConnectionStatus> TestAria2ConnectionAsync(CancellationToken cancellationToken = default);
}
