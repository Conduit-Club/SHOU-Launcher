using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using PCL.Core.App;

namespace PCL;

public partial class ClubBackdrop : UserControl
{
    private static readonly string[] Pictures = ["campus-lake.jpg", "spawn-plaza.jpg", "spawn-statues.jpg"];
    private readonly DispatcherTimer _carousel = new() { Interval = TimeSpan.FromSeconds(15) };
    private BitmapImage[] _images = [];
    private int _index;

    public ClubBackdrop()
    {
        InitializeComponent();
        Loaded += (_, _) => { LoadPictures(); Refresh(); };
        Unloaded += (_, _) => _carousel.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) Refresh(); else _carousel.Stop(); };
        _carousel.Tick += (_, _) => ShowNext();
    }

    private void Refresh()
    {
        Visibility = Config.Preference.Background.ClubHomeSlideshow ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible) { _carousel.Stop(); return; }
        CurrentImage.Source = _images[_index];
        _carousel.Start();
    }

    private void LoadPictures()
    {
        var images = new List<BitmapImage>();
        string[] paths;
        try { paths = JsonSerializer.Deserialize<string[]>(Config.Preference.Background.ClubHomePictures) ?? []; }
        catch (JsonException) { paths = []; }
        foreach (var path in paths.Where(File.Exists))
            try { images.Add(LoadPicture(new Uri(Path.GetFullPath(path)))); }
            catch (Exception ex) { ModBase.Log(ex, "[Conduit] 跳过无法读取的轮播图片", ModBase.LogLevel.Debug); }
        if (images.Count == 0)
            images.AddRange(Pictures.Select(name => LoadPicture(new Uri($"pack://application:,,,/Plain Craft Launcher 2;component/Images/Conduit/{name}"))));
        _images = images.ToArray();
        _index = 0;
        PreviousImage.Source = null;
    }

    private static BitmapImage LoadPicture(Uri uri)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.DecodePixelWidth = 1600;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ShowNext()
    {
        PreviousImage.Source = CurrentImage.Source;
        _index = (_index + 1) % _images.Length;
        CurrentImage.Source = _images[_index];
        CurrentImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.2)));
    }
}
