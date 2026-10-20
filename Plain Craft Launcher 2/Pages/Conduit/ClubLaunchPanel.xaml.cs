using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using PCL.Core.App;
using PCL.Core.Conduit;
namespace PCL;
public partial class ClubLaunchPanel : UserControl
{
    private bool _ready;
    private ServerChoice[] _serverChoices = [];
    public sealed class ServerChoice(ClubServer server, bool selected) : INotifyPropertyChanged
    {
        public string Id => server.Id;
        public string Name => server.Name;
        public string Detail => server.Id == "none" ? "不自动连接服务器" : server.Address + " · " + (server.Id == "smp" ? "1.21.1" : "1.21.11");
        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public ClubLaunchPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var selected = ClubCatalog.FindServer(Config.System.ClubServer);
            _serverChoices = ClubCatalog.Servers.Select(s => new ServerChoice(s, s.Id == selected.Id)).ToArray();
            Servers.ItemsSource = _serverChoices;
            UpdateDestination(selected);
            _ready = true;

        };
        Unloaded += (_, _) => { _ready = false;  };

    }
    private void Server_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string id }) return;
        var server = ClubCatalog.FindServer(id);
        foreach (var choice in _serverChoices) choice.Selected = choice.Id == server.Id;
        Config.System.ClubServer = server.Id;
        UpdateDestination(server);
    }
    private void UpdateDestination(ClubServer server) => DestinationHint.Text = server.Id == "none"
        ? "当前选择：只启动游戏，不自动入服。"
        : $"启动后前往 {server.Name}，实际版本 {(server.Id == "smp" ? "1.21.1" : "1.21.11")}。";
}
