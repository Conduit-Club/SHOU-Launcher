using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.Conduit;

namespace PCL;

public partial class ClubAria2Settings : UserControl
{
    public ClubAria2Settings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Enabled.Checked = Config.Download.Aria2Enabled;
            Endpoint.Text = Config.Download.Aria2Endpoint;
            Secret.Password = Config.Download.Aria2Secret;
        };
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = Aria2RpcClient.ParseEndpoint(Endpoint.Text.Trim());
            Config.Download.Aria2Endpoint = endpoint.AbsoluteUri;
            Config.Download.Aria2Secret = Secret.Password;
            Config.Download.Aria2Enabled = Enabled.Checked == true;
            Status.Text = "已保存，新下载任务生效。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void Test(object sender, RoutedEventArgs e)
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
