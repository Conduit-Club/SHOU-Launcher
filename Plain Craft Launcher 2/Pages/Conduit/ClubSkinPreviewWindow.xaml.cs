using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using PCL.Core.App;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.UI.Controls;

namespace PCL;

public partial class ClubSkinPreviewWindow
{
    private readonly McProfile _profile;
    private readonly Func<McProfile, CancellationToken, Task<SkinPreviewData>> _loader;
    private CancellationTokenSource? _loadCancellation;
    private SkinPreviewData? _data;
    private FrameworkElement? _host;
    private Point? _dragStart;
    private bool _isClosed;
    private double _zoom = 1;

    public ClubSkinPreviewWindow(ModMain.MyMsgBoxConverter converter) : this((McProfile)converter.Content) { }

    public ClubSkinPreviewWindow(McProfile profile,
        Func<McProfile, CancellationToken, Task<SkinPreviewData>>? loader = null)
    {
        // No access/refresh tokens are needed by the public texture API.
        _profile = new McProfile { ProfileType = profile.ProfileType, ProfileId = profile.ProfileId,
            Uuid = profile.Uuid, UserName = profile.UserName };
        _loader = loader ?? MicrosoftSkinPreview.LoadAsync;
        InitializeComponent();
        ProfileName.Text = _profile.UserName + " · 微软正版";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static void ShowCurrent()
    {
        if (!Config.Launch.SkinPreview3D || ProfileService.Current is not { ProfileType: ProfileType.Microsoft } profile)
            return;
        if (ModMain.frmMain.PanMsg.Children.OfType<ClubSkinPreviewWindow>().Any()
            || ModMain.WaitingMyMsgBox.Any(m => m.Type == ModMain.MyMsgBoxType.ClubSkinPreview)) return;
        ModMain.WaitingMyMsgBox.Add(new ModMain.MyMsgBoxConverter
        {
            Type = ModMain.MyMsgBoxType.ClubSkinPreview,
            Content = profile.Clone()
        });
        ModMain.MyMsgBoxTick();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        _host = Parent as FrameworkElement;
        if (_host is not null) _host.SizeChanged += Host_SizeChanged;
        UpdateBounds();
        if (ModMain.frmMain is { } main)
        {
            ModAnimation.AniStart(ModAnimation.AaColor(main.PanMsgBackground, BlurBorder.BackgroundProperty,
                new ModBase.MyColor(90d, 0d, 0d, 0d) - main.PanMsgBackground.Background, 200),
                "PanMsgBackground Background");
        }
        ViewportHost.Focus();
        await RefreshAsync();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateBounds();

    private void UpdateBounds()
    {
        var width = _host?.ActualWidth ?? ModMain.frmMain?.ActualWidth ?? 0;
        var height = _host?.ActualHeight ?? ModMain.frmMain?.ActualHeight ?? 0;
        if (width <= 0 || height <= 0) return;
        MaxWidth = Math.Max(260, width - Margin.Left - Margin.Right);
        MaxHeight = Math.Max(200, height - Margin.Top - Margin.Bottom);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isClosed = true;
        _loadCancellation?.Cancel();
        if (_host is not null) _host.SizeChanged -= Host_SizeChanged;
        ViewportHost.ReleaseMouseCapture();
    }

    private async Task RefreshAsync()
    {
        _loadCancellation?.Cancel();
        using var request = new CancellationTokenSource();
        _loadCancellation = request;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在加载当前装备的皮肤与披风……";
        try
        {
            var data = await _loader(_profile, request.Token);
            if (_isClosed || request.IsCancellationRequested) return;
            _data = data;
            CapeToggle.IsEnabled = data.Cape is not null;
            RebuildModel();
            EmptyText.Visibility = Visibility.Collapsed;
            StatusText.Text = (data.IsDefaultSkin ? "未设置自定义皮肤，显示内置默认模型" : data.IsSlim ? "纤细模型" : "经典模型")
                + (data.CapeLoadFailed ? " · 披风加载失败，可点击刷新重试" : data.Cape is null ? " · 当前未装备披风" : " · 已加载当前披风");
        }
        catch (OperationCanceledException) when (_isClosed || request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_isClosed) return;
            ModBase.Log(ex, "[SkinPreview] 皮肤预览加载失败", ModBase.LogLevel.Debug);
            StatusText.Text = _data is null ? "无法加载皮肤与披风，请检查网络后重试。" : "刷新失败，仍显示上次预览，可点击刷新重试。";
            EmptyText.Text = "暂时无法加载皮肤";
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, request))
            {
                _loadCancellation = null;
                if (!_isClosed) RefreshButton.IsEnabled = true;
            }
        }
    }

    private void RebuildModel()
    {
        if (_data is null) return;
        PlayerModel.Content = MinecraftSkinModel.Create(_data, OuterLayerToggle.IsChecked == true,
            CapeToggle.IsChecked == true);
    }

    private void OptionsChanged(object sender, RoutedEventArgs e) => RebuildModel();
    private async void Refresh_Click(object sender, MouseButtonEventArgs e) => await RefreshAsync();
    private void Reset_Click(object sender, MouseButtonEventArgs e) => ResetView();

    private void ResetView()
    {
        Yaw.Angle = -25;
        Pitch.Angle = -8;
        _zoom = 1;
        UpdateCamera();
    }

    private void Zoom(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.5, 2.5);
        UpdateCamera();
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCamera();

    private void UpdateCamera()
    {
        if (Camera is null) return;
        // WPF's field of view is horizontal. Fit the full player in short/wide viewports too.
        var aspect = ViewportHost.ActualWidth / Math.Max(1, ViewportHost.ActualHeight);
        var distance = 19 / Math.Tan(Camera.FieldOfView * Math.PI / 360) * Math.Max(1, aspect) * _zoom;
        Camera.Position = new Point3D(0, 3, distance);
        Camera.LookDirection = new Vector3D(0, -3, -distance);
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ViewportHost);
        ViewportHost.Focus();
        ViewportHost.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        ViewportHost.ReleaseMouseCapture();
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(ViewportHost);
        Yaw.Angle = (Yaw.Angle + (position.X - start.X) * 0.6) % 360;
        Pitch.Angle = Math.Clamp(Pitch.Angle + (position.Y - start.Y) * 0.4, -70, 70);
        _dragStart = position;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Zoom(Math.Pow(0.9, e.Delta / 120d));
        e.Handled = true;
    }

    private void Viewport_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Yaw.Angle -= 15; break;
            case Key.Right: Yaw.Angle += 15; break;
            case Key.Up: Pitch.Angle = Math.Max(-70, Pitch.Angle - 10); break;
            case Key.Down: Pitch.Angle = Math.Min(70, Pitch.Angle + 10); break;
            case Key.Add or Key.OemPlus: Zoom(0.9); break;
            case Key.Subtract or Key.OemMinus: Zoom(1.1); break;
            case Key.Home: ResetView(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Close_Click(object sender, MouseButtonEventArgs e) => ClosePreview();

    public void ClosePreview()
    {
        if (_isClosed) return;
        _isClosed = true;
        _loadCancellation?.Cancel();
        if (Parent is Panel parent) parent.Children.Remove(this);
        ModMain.MyMsgBoxTick();
    }
}
