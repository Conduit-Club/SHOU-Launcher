namespace SHOU.Launcher.Core.Abstractions;

public enum OfflineUuidMode { Standard, Legacy, Custom }

public interface IOfflineAccountService
{
    Task<AccountProfile> AddOfflineAccountAsync(string playerName, OfflineUuidMode mode, string? customUuid = null,
        CancellationToken cancellationToken = default);
}
