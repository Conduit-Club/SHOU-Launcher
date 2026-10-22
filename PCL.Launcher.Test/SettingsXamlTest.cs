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
using PCL.Core.Conduit;

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
        // The early startup path must also use Chinese before configuration is initialized.
        var initialUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            LocalizationService.ApplyFromConfig();
            Assert.AreEqual("zh-CN", LocalizationService.CurrentLanguage.Code);
        }
        finally { CultureInfo.CurrentUICulture = initialUiCulture; }
        var config = new ConfigService();
        config.StartAsync().GetAwaiter().GetResult();
        Assert.IsFalse(Config.System.Telemetry, "新配置必须默认关闭遥测，不能因移除弹窗而自动同意。");
        Assert.IsTrue(Config.System.TelemetryConfig.IsDefault(), "默认关闭无需写入用户授权。");

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
                (nameof(ClubSourceCategorySettings), () => new ClubSourceCategorySettings()),
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
            var tabs = (TabControl)sources.FindName("CategoryTabs");
            Assert.HasCount(3, tabs.Items);
            var categories = new[] { SourceCategory.Minecraft, SourceCategory.Mod, SourceCategory.Loader };
            var sourcePages = new[] { "MinecraftSources", "ModSources", "LoaderSources" }
                .Select(name => (ClubSourceCategorySettings)sources.FindName(name)).ToArray();
            for (var index = 0; index < sourcePages.Length; index++)
            {
                tabs.SelectedIndex = index;
                var page = sourcePages[index];
                var sourceList = (ListBox)page.FindName("Sources");
                var count = sourceList.Items.Count;
                Assert.IsGreaterThan(0, count);
                Assert.IsTrue(sourceList.Items.Cast<ListBoxItem>().All(i => ((DownloadSource)i.Tag).Category == categories[index]));
                ClickBoundButton(sources, "添加源");
                Assert.HasCount(count + 1, sourceList.Items);
                Assert.AreEqual(categories[index], ((DownloadSource)((ListBoxItem)sourceList.SelectedItem).Tag).Category);
                ClickBoundButton(sources, "删除所选");
                Assert.HasCount(count, sourceList.Items);
                ((MyTextBox)page.FindName("SourceName")).Text = "分类测试-" + categories[index];
                ((MyTextBox)page.FindName("Priority")).Text = (41 + index).ToString();
            }
            // Resetting the loader page must preserve pending Minecraft and Mod edits.
            ClickBoundButton(sources, "恢复本页默认");
            Assert.AreEqual("分类测试-Minecraft", ((MyTextBox)sourcePages[0].FindName("SourceName")).Text);
            Assert.AreEqual("分类测试-Mod", ((MyTextBox)sourcePages[1].FindName("SourceName")).Text);
            ClickBoundButton(sources, "保存下载源设置");
            StringAssert.Contains(((TextBlock)sources.FindName("Status")).Text, "已保存");
            var savedSources = DownloadSources.Parse(Config.Download.Sources);
            Assert.HasCount(DownloadSources.Defaults().Count, savedSources);
            Assert.IsTrue(savedSources.Any(s => s.Category == SourceCategory.Minecraft && s.Name == "分类测试-Minecraft" && s.Priority == 41));
            Assert.IsTrue(savedSources.Any(s => s.Category == SourceCategory.Mod && s.Name == "分类测试-Mod" && s.Priority == 42));
            Assert.IsFalse(savedSources.Any(s => s.Name == "分类测试-Loader"));

            // Validation on another tab must expose its editor without writing a partial configuration.
            var previousConfig = Config.Download.Sources;
            ((MyTextBox)sourcePages[1].FindName("Priority")).Text = "invalid";
            tabs.SelectedIndex = 2;
            ClickBoundButton(sources, "保存下载源设置");
            Assert.AreEqual(1, tabs.SelectedIndex);
            Assert.AreEqual(previousConfig, Config.Download.Sources);
            StringAssert.Contains(((TextBlock)sources.FindName("Status")).Text, "优先级必须为整数");
            ((MyTextBox)sourcePages[1].FindName("Priority")).Text = "42";
            ClickBoundButton(sources, "保存下载源设置");

            var reloaded = new ClubSourceSettings();
            reloaded.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var reloadedMod = (ClubSourceCategorySettings)reloaded.FindName("ModSources");
            Assert.AreEqual("分类测试-Mod", ((MyTextBox)reloadedMod.FindName("SourceName")).Text);
            Assert.HasCount(2, ((MyComboBox)reloadedMod.FindName("Layout")).Items);

            tabs.SelectedIndex = 1;
            ClickBoundButton(sources, "添加源");
            ((MyTextBox)sourcePages[1].FindName("SourceName")).Text = "自定义 Mod 镜像";
            ((MyTextBox)sourcePages[1].FindName("BaseUrl")).Text = "https://mods.example.test";
            ((MyTextBox)sourcePages[1].FindName("Origin")).Text = "https://api.modrinth.com";
            tabs.SelectedIndex = 0;
            ClickBoundButton(sources, "保存下载源设置");
            var addedMod = DownloadSources.Parse(Config.Download.Sources).Single(s => s.Name == "自定义 Mod 镜像");
            Assert.AreEqual(SourceCategory.Mod, addedMod.Category);
            Assert.AreEqual("https://mods.example.test", addedMod.BaseUrl);
            Assert.HasCount(DownloadSources.Defaults().Count + 1, DownloadSources.Parse(Config.Download.Sources));

            var previewDirectory = Environment.GetEnvironmentVariable("SHOU_SETTINGS_PREVIEW_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(previewDirectory))
            {
                Directory.CreateDirectory(previewDirectory);
                for (var index = 0; index < 3; index++)
                {
                    tabs.SelectedIndex = index;
                    sources.Measure(new Size(900, 1000));
                    sources.Arrange(new Rect(0, 0, 900, 1000));
                    sources.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 1000, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(sources);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(previewDirectory, $"sources-{categories[index]}.png"));
                    encoder.Save(file);
                }
            }

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
        element.UpdateLayout();
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
