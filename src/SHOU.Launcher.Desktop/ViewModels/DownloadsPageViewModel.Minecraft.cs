using System.Collections.ObjectModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record MinecraftPackageRow(string Version, MinecraftPackage Package)
{
    public string Title => Package.Title;
    public string Detail => $"{Package.FileName} · {Package.Size / 1048576d:0.00} MiB";
    public string Checksum => "SHA-1 " + Package.Sha1;
}

public sealed partial class DownloadsPageViewModel
{
    private bool _isPackageDetail;
    public bool IsPackageDetail => _isPackageDetail;
    public string CatalogPurpose => SelectedCategory.Id == "installer-minecraft"
        ? "选择 Minecraft 版本，下载客户端或服务端 JAR。"
        : "选择 Minecraft 版本，下载并安装基础游戏；也可选择兼容的加载器。";
    public ObservableCollection<MinecraftPackageRow> MinecraftPackages { get; } = [];
    public void OnNavigatedTo()
    {
        if (!_catalogLoaded && !_loading && IsCatalogPage && ReferenceEquals(Owner.SelectedPage, this) && RefreshCatalogCommand.CanExecute(null))
            RefreshCatalogCommand.Execute(null);
    }
    public async Task OpenPackageVersionAsync(GameCatalogVersion version)
    {
        if (!CanEdit || !version.CanInstall || SelectedCategory.Id != "installer-minecraft") return;
        var revision = ++_requestRevision;
        _selectedVersion = version; _isPackageDetail = true; _isConfiguration = false; _isResourceDetail = false;
        MinecraftPackages.Clear(); OnPropertyChanged(nameof(SelectedVersionId)); NotifyPage();
        if (Service is not IMinecraftPackageService packages) { RequestStatus = "当前服务尚未支持独立游戏文件下载。"; return; }
        await QueryAsync("读取 Minecraft 文件", revision, async ct =>
        {
            var files = await packages.GetMinecraftPackagesAsync(version.Id, ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision) return;
            foreach (var file in files) MinecraftPackages.Add(new(version.Id, file));
            RequestStatus = files.Count == 0 ? "此历史版本未提供独立客户端或服务端文件。" : "选择文件保存位置。下载完成后会校验大小与 SHA-1。";
        });
    }
    public async Task SaveMinecraftPackageAsync(MinecraftPackageRow row, string destination)
    {
        if (!CanEdit || Service is not IMinecraftPackageService packages) return;
        var success = await Owner.RunAsync("下载 Minecraft 文件", ct => packages.DownloadMinecraftPackageAsync(row.Version, row.Package.Kind, destination, ct));
        RequestStatus = success ? "Minecraft 文件已下载并通过校验。" : Owner.ErrorMessage ?? "下载已取消，可重新选择文件重试。";
    }
}
