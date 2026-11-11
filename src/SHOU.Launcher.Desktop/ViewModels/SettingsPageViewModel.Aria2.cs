using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private string aria2ConnectionSummary = "默认连接 http://localhost:6800/jsonrpc；下载前检查连接，未连接时使用内置下载器。";
    public string Aria2ConnectionSummary { get => aria2ConnectionSummary; private set => SetProperty(ref aria2ConnectionSummary, value); }
    public AsyncRelayCommand TestAria2ConnectionCommand { get; private set; } = null!;
    private void InitializeAria2Connection()
    {
        TestAria2ConnectionCommand = AsyncCommand(async () =>
        {
            Aria2ConnectionStatus? result = null;
            if (await Owner.RunAsync("测试 aria2 连接", async ct => result = await ((IAria2ConnectionService)Service).TestAria2ConnectionAsync(ct)))
                Aria2ConnectionSummary = result!.Message + (result.Version is { } version ? " · v" + version : "");
        }, () => CanEdit && !IsDirty && Service is IAria2ConnectionService);
    }
}
