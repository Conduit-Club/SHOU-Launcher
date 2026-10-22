using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Java;

namespace PCL;

public partial class ClubJavaTrustSettings : UserControl
{
    private sealed record Row(string Runtime, JavaTrustReport Report)
    {
        public override string ToString() => $"{Runtime}\n{Report}";
    }
    private bool _busy;
    public ClubJavaTrustSettings()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            AutoRepair.Checked = Config.Download.JavaTrustAutoRepair;
            await ScanAsync(false);
        };
    }

    private void Save(object sender, MouseButtonEventArgs e)
    {
        Config.Download.JavaTrustAutoRepair = AutoRepair.Checked == true;
        Status.Text = "已保存，新下载的 Java 将按此设置处理。";
    }

    private async void Refresh(object sender, MouseButtonEventArgs e) => await ScanAsync(true);

    private async Task ScanAsync(bool discover)
    {
        if (_busy) return;
        _busy = true; Actions.IsEnabled = false;
        Status.Text = "正在检查 Java 证书…";
        try
        {
            var manager = JavaService.JavaManager;
            if (manager is null) { Results.ItemsSource = Array.Empty<Row>(); Status.Text = "Java 管理器尚未就绪，请稍后重新扫描。"; return; }
            if (discover) await manager.ScanJavaAsync(force: true);
            var installations = manager.GetSortedJavaList().Select(e => e.Installation).ToArray();
            var rows = await Task.Run(() => installations.SelectMany(j => JavaTrustRepair.ScanJava(j.JavaFolder, j.MajorVersion)
                .Select(r => new Row($"{j.Brand} {(j.IsJre ? "JRE" : "JDK")} {j.Version}", r)))
                .DistinctBy(r => r.Report.Path, StringComparer.OrdinalIgnoreCase).ToArray());
            Results.ItemsSource = rows;
            Results.SelectedIndex = rows.Length > 0 ? 0 : -1;
            Status.Text = $"检查 {installations.Length} 个 Java：{rows.Count(r => r.Report.NeedsRepair && r.Report.CanRepair)} 项可修复，{rows.Count(r => !r.Report.CanRepair)} 项需人工处理。未命中仅表示未发现本规则中的证书。";
        }
        catch (Exception ex) { Status.Text = "检查失败：" + ex.Message; }
        finally { _busy = false; Actions.IsEnabled = true; }
    }

    private async void RepairSelected(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is not Row row) { Status.Text = "请先选择需要修复的条目。"; return; }
        await ProcessAsync([row], false);
    }

    private async void RepairAll(object sender, MouseButtonEventArgs e) =>
        await ProcessAsync(Results.Items.Cast<Row>().Where(r => r.Report.NeedsRepair && r.Report.CanRepair).ToArray(), false);

    private async void RestoreSelected(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is not Row row || !row.Report.CanRestore) { Status.Text = "请先选择有备份的条目。"; return; }
        await ProcessAsync([row], true);
    }

    private async Task ProcessAsync(Row[] rows, bool restore)
    {
        if (_busy) return;
        if (rows.Length == 0) { Status.Text = "没有可处理的条目。"; return; }
        _busy = true; Actions.IsEnabled = false;
        Status.Text = restore ? "正在校验并恢复备份…" : "正在备份并修复…";
        var messages = new List<string>();
        try
        {
            var updated = await Task.Run(() => rows.Select(row =>
            {
                try
                {
                    var report = restore ? JavaTrustRepair.Restore(row.Report) : JavaTrustRepair.Repair(row.Report);
                    messages.Add($"{(restore ? "已恢复" : "已修复")}：{report.Path}");
                    return row with { Report = report };
                }
                catch (Exception ex) { messages.Add($"未处理：{row.Report.Path}\n{ex.Message}"); return row; }
            }).ToArray());
            var replacements = updated.ToDictionary(r => r.Report.Path, StringComparer.OrdinalIgnoreCase);
            Results.ItemsSource = Results.Items.Cast<Row>().Select(r => replacements.GetValueOrDefault(r.Report.Path, r)).ToArray();
            Results.SelectedIndex = Results.Items.Count > 0 ? 0 : -1;
            Status.Text = string.Join("\n", messages) + "\n请重启正在运行的 Java / 游戏。";
        }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
        finally { _busy = false; Actions.IsEnabled = true; }
    }
}
