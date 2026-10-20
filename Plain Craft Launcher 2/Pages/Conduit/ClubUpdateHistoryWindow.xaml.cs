using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.UI.Controls;

namespace PCL;

/// <summary>显示社团更新日志的原生 PanMsg 内容，不创建额外的系统窗口。</summary>
public partial class ClubUpdateHistoryWindow
{
    private readonly ObservableCollection<ClubUpdateHistoryItem> _items = [];
    private readonly int _uuid = ModBase.GetUuid();
    private bool _isClosed;

    public ClubUpdateHistoryWindow(ModMain.MyMsgBoxConverter _)
    {
        InitializeComponent();
        HistoryItems.ItemsSource = _items;
        SetHistory(ClubUpdates.History, ClubUpdates.History.Count == 0
            ? "缓存加载中……"
            : "显示缓存，刷新中……");
        ClubUpdates.RegisterHistoryWindow(this);
        Loaded += Load;
    }

    public void SetHistory(IReadOnlyList<ClubUpdateHistoryItem> items, string status)
    {
        if (_isClosed)
            return;
        _items.Clear();
        foreach (var item in items.OrderByDescending(item => item.Notice.Date))
            _items.Add(item);
        SetStatus(status);
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryItems.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetStatus(string status)
    {
        if (!_isClosed)
            StatusText.Text = status;
    }

    public void SetRefreshing(bool isRefreshing)
    {
        if (_isClosed)
            return;
        RefreshButton.IsEnabled = !isRefreshing;
        RefreshButton.Text = isRefreshing ? "刷新中…" : "刷新";
    }

    private void Load(object sender, RoutedEventArgs e)
    {
        try
        {
            Opacity = 0d;
            ModAnimation.AniStart(
                ModAnimation.AaColor(ModMain.frmMain.PanMsgBackground, BlurBorder.BackgroundProperty,
                    new ModBase.MyColor(90d, 0d, 0d, 0d) - ModMain.frmMain.PanMsgBackground.Background, 200),
                "PanMsgBackground Background");
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaOpacity(this, 1d, 120, 60),
                    ModAnimation.AaDouble(i => TransformPos.Y += (double)i, -TransformPos.Y,
                        300, 60, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i, -TransformRotate.Angle,
                        300, 60, new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
                }, "MyMsgBox " + _uuid);
            ModBase.Log("[Control] 社团更新日志弹窗");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "社团更新日志弹窗加载失败", ModBase.LogLevel.Hint);
        }
        ClubUpdates.HistoryWindowLoaded(this);
    }

    private void Refresh_Click(object sender, MouseButtonEventArgs e)
        => ClubUpdates.RefreshHistory(this);

    private void OpenArticle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Uri uri })
            ModBase.OpenWebsite(uri.ToString());
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
        => CloseHistory();

    /// <summary>由 Escape 或关闭按钮调用，沿用 MyMsg 的遮罩动画和队列推进。</summary>
    public void CloseHistory()
    {
        if (_isClosed)
            return;
        _isClosed = true;
        IsHitTestVisible = false;
        ClubUpdates.UnregisterHistoryWindow(this);
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(this, -Opacity, 80, 20),
            ModAnimation.AaDouble(i => TransformPos.Y += (double)i, 20d - TransformPos.Y,
                150, 0, new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i, 6d - TransformRotate.Angle,
                150, 0, new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() =>
            {
                if (Parent is Panel parent)
                    parent.Children.Remove(this);
                ModMain.MyMsgBoxTick();
            }, after: true)
        }, "MyMsgBox " + _uuid);
    }
}
