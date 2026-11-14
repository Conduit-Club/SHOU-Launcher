using System.Globalization;
using Avalonia.Data.Converters;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Views;

public sealed class SettingsPreferenceLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = value?.ToString() switch
        {
        "zh-CN" => "简体中文", "zh-TW" => "繁體中文", "en-US" => "English", "en-GB" => "English (United Kingdom)", "ja-JP" => "日本語", "fr-FR" => "Français", "es-ES" => "Español", "system" => "跟随系统",
        "System" => "跟随系统", "Light" => "浅色", "Dark" => "深色",
        "default" => "默认", "none" => "无", "text" => "文本", "image" => "图片", "local" => "本地文件", "remote" => "网络地址", "" => "默认",
        "sky" => "天空蓝", "cat" => "龙猫蓝", "death" => "死机蓝", "garnet" => "嘉然配色", "aimisi" => "爱弥斯配色", "elegant" => "高雅人士配色", "blue" => "海蓝", "jade" => "青玉",
        "empty" => "空白", "smart" => "智能填充", "center" => "居中", "fit" => "完整适应", "stretch" => "拉伸", "tile" => "平铺",
        "top-left" => "左上", "top-right" => "右上", "bottom-left" => "左下", "bottom-right" => "右下",
        "keep" => "保持播放状态", "stop" => "游戏运行时停止", "only" => "仅在游戏运行时播放",
        "off" => "不提示", "club-updates" => "社团更新日志",
        "X86" => "32 Bit", "X64" => "64 Bit", "Arm64" => "ARM64",
        "configured" => "用户选择", "managed" => "启动器管理", "portable" => "便携目录", "registry" => "注册表", "installed" => "系统安装",
        "MinecraftRuntime" => "Minecraft 运行环境",
        "All" => "隔离所有实例", "Modded" => "仅隔离模组实例", "Shared" => "不隔离（共用游戏目录）", "Current" => "保持当前游戏目录", "FollowGlobal" => "跟随全局隔离设置", "Isolated" => "独立游戏目录",
        "Keep" => "游戏启动后仍保持不变", "Minimize" => "游戏启动后最小化", "Hide" => "游戏启动后隐藏，退出后恢复", "Close" => "游戏启动后关闭启动器",
        "Normal" => "中（平衡）", "BelowNormal" => "低", "AboveNormal" => "较高", "High" => "高",
        "JavaDefault" => "Java 默认", "IPv4" => "优先 IPv4", "IPv6" => "优先 IPv6",
        "Custom" => "使用下方自定义来源顺序", "OfficialFirst" => "优先官方，失败后使用镜像", "MirrorFirst" => "优先镜像，失败后使用官方", _ => value
        };
        return label is string phrase ? Localization.UiLocalization.Instance.Text(phrase) : label;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
