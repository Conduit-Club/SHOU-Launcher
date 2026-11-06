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
    private GameCatalogVersion? _packageVersion;
    public bool IsPackageDetail => _isPackageDetail;
    public string PackageVersionId => _packageVersion?.Id ?? "";
    public string CatalogPurpose => SelectedCategory.Id == "installer-minecraft"
        ? "选择 Minecraft 版本，下载客户端或服务端 JAR。"
        : "选择 Minecraft 版本，下载并安装基础游戏；也可选择兼容的加载器。";
    public ObservableCollection<MinecraftPackageRow> MinecraftPackages { get; } = [];
    public void OnNavigatedTo()
    {
        if (!_catalogLoaded && !_catalogRequestActive && IsCatalogPage && ReferenceEquals(Owner.SelectedPage, this))
            _catalogRequested = true;
        TryStartCatalogRequest();
    }
    private void TryStartCatalogRequest()
    {
        if (!_catalogRequested || _catalogLoaded || _catalogRequestActive || _loading || !CanQuery
            || !IsCatalogPage || !ReferenceEquals(Owner.SelectedPage, this)) return;
        _catalogRequested = false;
        _ = RefreshCatalogAsync(false);
    }
    public async Task OpenPackageVersionAsync(GameCatalogVersion version)
    {
        if (!CanEdit || !version.CanInstall || SelectedCategory.Id != "installer-minecraft") return;
        var revision = ++_requestRevision;
        _packageVersion = version; _isPackageDetail = true; _isConfiguration = false; _isResourceDetail = false;
        MinecraftPackages.Clear(); OnPropertyChanged(nameof(PackageVersionId)); NotifyPage();
        if (Service is not IMinecraftPackageService packages) { SetRequestStatus("当前服务尚未支持独立游戏文件下载。"); return; }
        await QueryAsync("读取 Minecraft 文件", revision, async ct =>
        {
            var files = await packages.GetMinecraftPackagesAsync(version.Id, ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision) return;
            foreach (var file in files) MinecraftPackages.Add(new(version.Id, file));
            SetRequestStatus(files.Count == 0 ? "此历史版本未提供独立客户端或服务端文件。" : "选择文件保存位置。下载完成后会校验大小与 SHA-1。", files.Count == 0);
        });
    }
    public async Task SaveMinecraftPackageAsync(MinecraftPackageRow row, string destination)
    {
        if (!CanEdit || Service is not IMinecraftPackageService packages) return;
        var success = await Owner.RunAsync("下载 Minecraft 文件", ct => packages.DownloadMinecraftPackageAsync(row.Version, row.Package.Kind, destination, ct));
        SetRequestStatus(success ? "Minecraft 文件已下载并通过校验。" : Owner.ErrorMessage ?? "下载已取消，可重新选择文件重试。");
    }
}
