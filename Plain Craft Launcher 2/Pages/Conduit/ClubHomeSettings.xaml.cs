using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCL.Core.App;

namespace PCL;

public partial class ClubHomeSettings : UserControl
{
    private bool _ready;
    public ClubHomeSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _ready = false;
            Intro.Checked = Config.Preference.Homepage.ClubIntro;
            Servers.Checked = Config.Preference.Homepage.ClubServers;
            UpdateStatus();
            _ready = true;
        };
    }

    private void Save(object sender, bool user)
    {
        if (!_ready) return;
        Config.Preference.Homepage.ClubIntro = Intro.Checked == true;
        Config.Preference.Homepage.ClubServers = Servers.Checked == true;
    }

    private void ChoosePictures(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dialog.ShowDialog() != true) return;
        Config.Preference.Background.ClubHomePictures = JsonSerializer.Serialize(dialog.FileNames);
        UpdateStatus();
    }

    private void ResetPictures(object sender, RoutedEventArgs e)
    {
        Config.Preference.Background.ClubHomePictures = "";
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        string[] paths;
        try { paths = JsonSerializer.Deserialize<string[]>(Config.Preference.Background.ClubHomePictures) ?? []; }
        catch (JsonException) { paths = []; }
        PicturesStatus.Text = paths.Length == 0 ? "当前使用内置社团实景。" : $"已选择 {paths.Length} 张图片，返回首页后生效。";
    }
}
