using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Minecraft;
using PCL.Core.UI;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageSetupJava
{
    private JavaEntry[] _entries = [];

    public ModLoader.LoaderTask<bool, List<JavaEntry>> loader;

    public PageSetupJava()
    {
        InitializeComponent();
        loader = new ModLoader.LoaderTask<bool, List<JavaEntry>>("JavaPageLoader", Load_GetJavaList);
        Loaded += PageSetupLaunch_Loaded;
    }

    private void PageSetupLaunch_Loaded(object sender, RoutedEventArgs e)
    {
        PageLoaderInit(PanLoad, CardLoad, PanMain, null, loader, _ => OnLoadFinished(), Load_Input);
    }

    private object Load_Input()
    {
        return false;
    }

    private void Load_GetJavaList(ModLoader.LoaderTask<bool, List<JavaEntry>> loader)
    {
        if (loader.input || loader.isForceRestarting)
        {
            JavaService.JavaManager.ScanJavaAsync(force: true).GetAwaiter().GetResult();
            JavaService.JavaManager.CheckAllAvailability();
        }
        loader.output = ModJava.Javas.GetSortedJavaList();
    }

    private void OnLoadFinished()
        => RenderJavaList(ModJava.Javas.GetSortedJavaList());

    private void RenderJavaList(IEnumerable<JavaEntry> entries)
    {
        var javas = entries.ToArray();
        _entries = javas;
        PanContent.Children.Clear();
        var itemAuto = new MyListItem
        {
            Type = MyListItem.CheckType.RadioBox,
            Title = Lang.Text("Setup.Java.AutoSelect.Title"),
            Info = Lang.Text("Setup.Java.AutoSelect.Info")
        };
        itemAuto.Check += (_, _) => { Config.Launch.SelectedJava = ""; UpdateSelectionSummary(javas); };
        PanContent.Children.Add(itemAuto);
        var currentSetJava = Config.Launch.SelectedJava;
        foreach (var entry in javas)
        {
            var item = ItemBuild(entry);
            PanContent.Children.Add(item);
            if (string.Equals(entry.Installation.JavaExePath, currentSetJava, StringComparison.OrdinalIgnoreCase))
                item.SetChecked(true, false, false);
        }

        if (string.IsNullOrEmpty(currentSetJava))
            itemAuto.SetChecked(true, false, false);
        UpdateSelectionSummary(javas);
    }

    private void UpdateSelectionSummary(IEnumerable<JavaEntry> entries)
    {
        var javas = entries.ToArray();
        var selected = Config.Launch.SelectedJava;
        JavaListCard.Title = $"已发现的 Java（{javas.Length}）";
        if (string.IsNullOrEmpty(selected))
        {
            SelectionSummary.Text = "自动选择 Java（推荐）";
            SelectionDetail.Text = "启动游戏时按实例要求选择兼容版本。全局选择可被实例单独设置覆盖。";
            JavaListCard.IsSwapped = true;
        }
        else
        {
            var entry = javas.FirstOrDefault(j => string.Equals(j.Installation.JavaExePath, selected, StringComparison.OrdinalIgnoreCase));
            var valid = entry is { IsEnabled: true } && entry.Installation.IsStillAvailable;
            SelectionSummary.Text = valid ? $"手动选择：Java {entry!.Installation.Version}" : "指定的 Java 不可用";
            SelectionDetail.Text = valid ? selected : $"{selected}\n请重新选择、添加 Java，或恢复自动选择。";
            JavaListCard.IsSwapped = false;
        }
    }

    private void SelectAutomatic(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Config.Launch.SelectedJava = "";
        RenderJavaList(_entries);
    }

    private void ShowJavaList(object sender, System.Windows.Input.MouseButtonEventArgs e) => JavaListCard.IsSwapped = false;
    
    private MyListItem ItemBuild(JavaEntry j)
    {
        var item = new MyListItem();
        var versionTypeDesc = j.Installation.IsJre ? "JRE" : "JDK";
        var versionNameDesc = j.Installation.MajorVersion.ToString();
        item.Title = $"{versionTypeDesc} {versionNameDesc}";

        item.Info = j.Installation.JavaFolder;
        var displayTags = new List<string>();
        var displayBits = j.Installation.Is64Bit ? "64 Bit" : "32 Bit";
        displayTags.Add(displayBits);
        var displayBrand = j.Installation.Brand.ToString();
        displayTags.Add(displayBrand);
        item.Tags = displayTags;

        item.Type = MyListItem.CheckType.RadioBox;
        item.Check += (sender, e) =>
        {
            if (!j.Installation.IsStillAvailable)
            {
                e.handled = true;
                HintService.Hint(Lang.Text("Setup.Java.Unavailable"));
                return;
            }

            if (j.IsEnabled)
            {
                Config.Launch.SelectedJava = j.Installation.JavaExePath;
                SelectionSummary.Text = $"手动选择：Java {j.Installation.Version}";
                SelectionDetail.Text = j.Installation.JavaExePath;
            }
            else
            {
                HintService.Hint(Lang.Text("Setup.Java.EnableBeforeSelect"));
                e.handled = true;
            }
        };
        var btnOpenFolder = new MyIconButton();
        btnOpenFolder.SvgIcon = "lucide/folder-open";
        btnOpenFolder.ToolTip = Lang.Text("Common.Action.Open");
        btnOpenFolder.Click += (sender, e) =>
        {
            if (!j.Installation.IsStillAvailable)
            {
                HintService.Hint(Lang.Text("Setup.Java.Unavailable"));
                return;
            }

            ModBase.OpenExplorer(j.Installation.JavaFolder);
        };
        var btnInfo = new MyIconButton();
        btnInfo.SvgIcon = "lucide/info";
        btnInfo.ToolTip = Lang.Text("Setup.Java.Detail.ToolTip");
        btnInfo.Click += (sender, e) =>
        {
            if (!j.Installation.IsStillAvailable)
            {
                HintService.Hint(Lang.Text("Setup.Java.Unavailable"));
                return;
            }

            ModMain.MyMsgBox(
                Lang.Text("Setup.Java.Info.Format",
                    versionTypeDesc,
                    j.Installation.Version.ToString(),
                    j.Installation.Architecture.ToString(),
                    displayBits,
                    displayBrand,
                    j.Installation.JavaFolder),
                Lang.Text("Setup.Java.Info.Title"));
        };
        var btnEnableSwitch = new MyIconButton();
        
        item.Buttons = [btnOpenFolder, btnInfo, btnEnableSwitch];

        void UpdateEnableStyle(bool isCurEnable)
        {
            if (!j.Installation.IsStillAvailable)
            {
                HintService.Hint(Lang.Text("Setup.Java.Unavailable"));
                return;
            }

            if (isCurEnable)
            {
                item.LabTitle.TextDecorations = null;
                item.LabTitle.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
                btnEnableSwitch.SvgIcon = "lucide/circle-minus";
                btnEnableSwitch.ToolTip = Lang.Text("Setup.Java.Disable");
            }
            else
            {
                item.LabTitle.TextDecorations = TextDecorations.Strikethrough;
                item.LabTitle.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
                btnEnableSwitch.SvgIcon = "lucide/circle-check";
                btnEnableSwitch.ToolTip = Lang.Text("Setup.Java.Enable");
            }
        }
        
        btnEnableSwitch.Click += (_, _) =>
        {
            try
            {
                var target = ModJava.Javas.AddOrGet(j.Installation.JavaExePath);
                if (target is null)
                {
                    HintService.Hint(Lang.Text("Setup.Java.Unavailable"));
                    return;
                }

                if (target.IsEnabled && string.Equals(Config.Launch.SelectedJava, target.Installation.JavaExePath, StringComparison.OrdinalIgnoreCase))
                {
                    HintService.Hint(Lang.Text("Setup.Java.DeselectBeforeDisable"));
                    return;
                }

                target.IsEnabled = !target.IsEnabled;
                UpdateEnableStyle(target.IsEnabled);
                ModJava.Javas.SaveConfig();
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    Lang.Text("Setup.Java.EnableFailed"),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Setup.Java.EnableFailed"));
            }
        };
        UpdateEnableStyle(j.IsEnabled);

        return item;
    }

    private void BtnAdd_Click(object sender, ModBase.RouteEventArgs e)
    {
        var ret = SystemDialogs.SelectFile(Lang.Text("Setup.Java.SelectFile.Filter"), Lang.Text("Setup.Java.SelectFile.Title"));
        if (string.IsNullOrEmpty(ret) || !File.Exists(ret))
            return;
        if (ModJava.Javas.Exist(ret))
            HintService.Hint(Lang.Text("Setup.Java.AlreadyExists"));
        else
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Run(() =>
                {
                    ModJava.Javas.AddOrGet(ret);
                    ModJava.Javas.SaveConfig();
                });
                if (ModJava.Javas.Exist(ret))
                {
                    HintService.Hint(Lang.Text("Setup.Java.Added"), HintType.Success);
                    loader.Start(true, true);
                }
                else
                {
                    HintService.Hint(Lang.Text("Setup.Java.AddFailed"), HintType.Error);
                }
            }));
    }
}
