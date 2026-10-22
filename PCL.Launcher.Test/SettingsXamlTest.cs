using System.IO;
using System.Reflection;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;

namespace PCL.Launcher.Test;

[TestClass]
[DoNotParallelize]
public sealed class SettingsXamlTest
{
    [STATestMethod]
    public void PersonalizationAndGameManagementLoadCompiledXaml()
    {
        // Isolate configuration from both the user's launcher and other test projects.
        var root = Path.Combine(Path.GetTempPath(), "SHOU-Settings-Xaml-Test", Guid.NewGuid().ToString("N"));
        Paths.Data = Path.Combine(root, "data");
        Paths.SharedData = Path.Combine(root, "shared");
        Paths.SharedLocalData = Path.Combine(root, "local");
        Paths.OldSharedData = Path.Combine(root, "legacy");
        Paths.Temp = Path.Combine(root, "temp");
        foreach (var path in new[] { Paths.Data, Paths.SharedData, Paths.SharedLocalData, Paths.Temp }) Directory.CreateDirectory(path);
        var app = new PCL.Application();
        Lifecycle.CurrentApplication = app;
        app.InitializeComponent();
        var config = new ConfigService();
        config.StartAsync().GetAwaiter().GetResult();

        var failures = new List<string>();
        var pages = new Dictionary<string, FrameworkElement>();
        // PageSetupLeft suspends config write-back while it constructs settings pages.
        ModAnimation.AniControlEnabled += 1;
        try
        {
            var previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                Assert.AreEqual("zh-CN", Config.Preference.Localization.Language);
                LocalizationService.ApplyFromConfig();
                Assert.AreEqual("zh-CN", LocalizationService.CurrentLanguage.Code);
                Config.Preference.Localization.Language = "en-US";
                LocalizationService.ApplyFromConfig();
                Assert.AreEqual("en-US", LocalizationService.CurrentLanguage.Code);
                Config.Preference.Localization.LanguageConfig.SetDefaultValue();
                LocalizationService.ApplyFromConfig();
                Assert.AreEqual("zh-CN", LocalizationService.CurrentLanguage.Code);
            }
            finally { CultureInfo.CurrentUICulture = previousUiCulture; }

            foreach (var (name, construct) in new (string, Func<FrameworkElement>)[]
            {
                (nameof(ClubHomeSettings), () => new ClubHomeSettings()),
                (nameof(ClubSourceSettings), () => new ClubSourceSettings()),
                (nameof(ClubAria2Settings), () => new ClubAria2Settings()),
                (nameof(ClubJavaTrustSettings), () => new ClubJavaTrustSettings()),
                (nameof(PageSetupJava), () => new PageSetupJava()),
                (nameof(PageSetupUI), () => new PageSetupUI()),
                (nameof(PageSetupGameManage), () => new PageSetupGameManage())
            })
            {
                try { pages[name] = construct(); }
                catch (Exception ex) { failures.Add($"{name}: {ex}"); }
            }
            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));

            var home = pages[nameof(ClubHomeSettings)];
            home.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Config.Preference.Background.ClubHomePictures = "[\"test.jpg\"]";
            ClickBoundButton(home, "恢复内置轮播");
            Assert.AreEqual("", Config.Preference.Background.ClubHomePictures);
            Assert.AreEqual(Config.Preference.Homepage.ClubServers, ((MyCheckBox)home.FindName("Servers")).Checked);

            var sources = pages[nameof(ClubSourceSettings)];
            sources.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var sourceList = (ListBox)sources.FindName("Sources");
            var count = sourceList.Items.Count;
            Assert.IsGreaterThan(0, count);
            ClickBoundButton(sources, "添加源");
            Assert.HasCount(count + 1, sourceList.Items);
            ClickBoundButton(sources, "删除所选");
            Assert.HasCount(count, sourceList.Items);
            ClickBoundButton(sources, "恢复默认");
            ClickBoundButton(sources, "保存下载源设置");
            StringAssert.Contains(((TextBlock)sources.FindName("Status")).Text, "已保存");

            var aria = pages[nameof(ClubAria2Settings)];
            aria.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            ((MyTextBox)aria.FindName("Endpoint")).Text = "http://127.0.0.1:16800/jsonrpc";
            ClickBoundButton(aria, "保存 RPC 设置");
            Assert.AreEqual("http://127.0.0.1:16800/jsonrpc", Config.Download.Aria2Endpoint);

            var trust = pages[nameof(ClubJavaTrustSettings)];
            ((MyCheckBox)trust.FindName("AutoRepair")).Checked = false;
            ClickBoundButton(trust, "保存自动修复设置");
            Assert.IsFalse(Config.Download.JavaTrustAutoRepair);
            ClickBoundButton(trust, "重新扫描 Java 证书");
            StringAssert.Contains(((TextBlock)trust.FindName("Status")).Text, "尚未就绪");
            ClickBoundButton(trust, "修复所选");
            StringAssert.Contains(((TextBlock)trust.FindName("Status")).Text, "请先选择");
            ClickBoundButton(trust, "修复全部可处理项");
            StringAssert.Contains(((TextBlock)trust.FindName("Status")).Text, "没有可处理");
            ClickBoundButton(trust, "恢复所选备份");
            StringAssert.Contains(((TextBlock)trust.FindName("Status")).Text, "有备份");

            var download = ModJava.GetJavaDownloadLoader();
            Assert.HasCount(3, download.loaders);
            Assert.AreEqual("检查并修复 Java 证书", download.loaders[^1].name);
            Assert.IsTrue(download.loaders[^1].block, "证书检查必须阻塞下载任务完成。");
        }
        finally { ModAnimation.AniControlEnabled -= 1; config.StopAsync().GetAwaiter().GetResult(); }
    }

    private static void ClickBoundButton(DependencyObject root, string text)
    {
        var element = (FrameworkElement)root;
        element.Measure(new Size(1000, 1600));
        element.Arrange(new Rect(0, 0, 1000, 1600));
        var buttons = Descendants(root).OfType<MyButton>().ToArray();
        var button = buttons.SingleOrDefault(b => b.Text == text);
        Assert.IsNotNull(button, $"找不到按钮 {text}；已加载：{string.Join(", ", buttons.Select(b => b.Text))}");
        // Invoke the delegate registered by the compiled XAML, without opening a native picker or sending pointer input.
        var handler = (MyButton.ClickEventHandler?)typeof(MyButton).GetField("Click", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button);
        Assert.IsNotNull(handler, text);
        handler(button, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
