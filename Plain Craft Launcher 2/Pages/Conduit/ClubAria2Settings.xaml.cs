using System.Net.Http;
using System.Windows.Input;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.Conduit;

namespace PCL;

public partial class ClubAria2Settings : UserControl
{
    private bool _loaded;
    private bool _loading;
    public ClubAria2Settings()
    {
        InitializeComponent();
        Endpoint.TextChanged += (_, _) => MarkEdited();
        Secret.PasswordChanged += (_, _) => MarkEdited();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            ReloadFromConfig();
        };
    }

    public void ReloadFromConfig()
    {
        _loading = true;
        Enabled.Checked = Config.Download.Aria2Enabled;
        Endpoint.Text = Config.Download.Aria2Endpoint;
        Secret.Password = Config.Download.Aria2Secret;
        RpcDetails.IsExpanded = Config.Download.Aria2Enabled;
        PendingStatus.Text = "没有未保存的修改";
        _loading = false;
        UpdateState();
    }

    private void MarkEdited()
    {
        if (!_loading) PendingStatus.Text = "有未保存的修改 · 保存后用于新下载任务";
    }

    private void UpdateState() => EffectiveState.Text = Config.Download.Aria2Enabled
        ? $"已启用，使用已保存的 RPC 配置：{Config.Download.Aria2Endpoint}"
        : "未启用，当前使用启动器内置下载器。";

    private void ChangeEnabled(object sender, bool user)
    {
        if (_loading || !_loaded) return;
        try
        {
            if (Enabled.Checked == true) Aria2RpcClient.ParseEndpoint(Config.Download.Aria2Endpoint);
            Config.Download.Aria2Enabled = Enabled.Checked == true;
            RpcDetails.IsExpanded = Enabled.Checked == true;
            UpdateState();
            Status.Text = "开关已保存。启用时使用已保存的地址与密钥；下方字段修改需另行保存。";
        }
        catch (Exception ex)
        {
            _loading = true;
            Enabled.Checked = Config.Download.Aria2Enabled;
            _loading = false;
            RpcDetails.IsExpanded = true;
            Status.Text = "请先保存有效的 RPC 地址：" + ex.Message;
        }
    }

    private void Save(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var endpoint = Aria2RpcClient.ParseEndpoint(Endpoint.Text.Trim());
            Config.Download.Aria2Endpoint = endpoint.AbsoluteUri;
            Config.Download.Aria2Secret = Secret.Password;
            Config.Download.Aria2Enabled = Enabled.Checked == true;
            PendingStatus.Text = "没有未保存的修改";
            UpdateState();
            Status.Text = "已保存，新下载任务生效。";
        }
        catch (Exception ex) { RpcDetails.IsExpanded = true; Status.Text = ex.Message; }
    }

    private async void Test(object sender, MouseButtonEventArgs e)
    {
        if (sender is MyButton button) button.IsEnabled = false;
        try
        {
            using var http = new HttpClient();
            var rpc = new Aria2RpcClient(http, Aria2RpcClient.ParseEndpoint(Endpoint.Text.Trim()), Secret.Password);
            Status.Text = "正在连接…";
            Status.Text = "已连接 aria2 " + await rpc.GetVersionAsync();
        }
        catch (Exception) { Status.Text = "连接失败，请核对 RPC 地址、端口与密钥，并确认 aria2 已启动。"; }
        finally { if (sender is MyButton b) b.IsEnabled = true; }
    }
}
