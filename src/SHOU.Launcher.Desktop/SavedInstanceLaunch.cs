using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop;

public static class SavedInstanceLaunch
{
    public static Task RunAsync(ILauncherService launcher, string instanceId, CancellationToken ct)
    {
        SafePath.Identifier(instanceId);
        if (!launcher.Snapshot.Instances.Any(i => i.Id == instanceId)) throw new InvalidOperationException("脚本对应的实例不存在，请在启动器中重新导出。");
        if (!launcher.Snapshot.Accounts.Any(a => a.Id == launcher.Snapshot.SelectedAccountId)) throw new InvalidOperationException("请先在启动器中登录并选择账户，再运行脚本。");
        return launcher.LaunchAsync(instanceId, cancellationToken: ct);
    }
}
