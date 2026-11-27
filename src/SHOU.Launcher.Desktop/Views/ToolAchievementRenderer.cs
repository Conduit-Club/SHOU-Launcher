using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SHOU.Launcher.Desktop.Views;

/// <summary>Shared, local PNG rendering. Uses existing licensed assets and the OS font; never uploads entered text.</summary>
public static class ToolAchievementRenderer
{
    public static byte[] Render(string item, string title, string line1, string line2, byte[]? customIcon = null)
    {
        foreach (var value in new[] { title, line1, line2 })
            if (value.Length > 45 || value.Any(c => c is < ' ' or > '~')) throw new ArgumentException("每行最多 45 个英文字符。");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(line1)) throw new ArgumentException("请填写成就名和第一行文字。");
        var legacy = LegacyAchievementIcons.Resolve(item);
        var asset = item.Trim().ToLowerInvariant() switch
        {
            "grass" or "grass_block" => "Grass", "anvil" or "145" => "Anvil",
            "command_block" or "137" => "CommandBlock", "egg" or "344" => "Egg",
            _ when customIcon is not null || legacy is not null => "Grass",
            _ => throw new ArgumentException("请选择旧物品库中的名称或 ID（1–39），也可选择图标 PNG。")
        };
        var library = customIcon is null && legacy is not null;
        using var input = customIcon is null ? AssetLoader.Open(new Uri("avares://SHOU.Launcher.Desktop/Assets/" + (library ? "AchievementIcons/" + legacy : "Minecraft/" + asset) + ".png")) : new MemoryStream(customIcon);
        if (customIcon is not null) ValidateIcon(customIcon);
        using var icon = new Bitmap(input);
        if (icon.PixelSize.Width > 512 || icon.PixelSize.Height > 512) throw new InvalidDataException("物品图标不得超过 512×512。");
        var height = string.IsNullOrWhiteSpace(line2) ? 112 : 144;
        var view = new AchievementControl(icon, title, line1, line2, library) { Width = 640, Height = height };
        view.Measure(new Size(640, height)); view.Arrange(new Rect(0, 0, 640, height));
        using var output = new RenderTargetBitmap(new PixelSize(640, height), new Vector(96, 96)); output.Render(view);
        using var stream = new MemoryStream(); output.Save(stream, new PngBitmapEncoderOptions()); return stream.ToArray();
    }
    public static void ValidateIcon(byte[] bytes)
    {
        if (bytes.Length is < 45 or > 2 * 1024 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("请选择 PNG 物品图标。");
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)); var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
        if (width is < 1 or > 512 || height is < 1 or > 512) throw new InvalidDataException("物品图标不得超过 512×512。");
    }
    private sealed class AchievementControl(Bitmap icon, string title, string first, string second, bool library) : Control
    {
        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#212121")), new Pen(new SolidColorBrush(Color.Parse("#777777")), 4), new Rect(4, 4, Bounds.Width - 8, Bounds.Height - 8), 5, 5);
            context.DrawImage(icon, library ? new Rect(16, 16, 32, 32) : new Rect(icon.Size), new Rect(20, (Bounds.Height - 64) / 2, 64, 64));
            Text(context, title, 104, 15, Brushes.Gold, 21); Text(context, first, 104, 47, Brushes.White, 20);
            if (second.Length != 0) Text(context, second, 104, 79, Brushes.LightGray, 20);
        }
        private static void Text(DrawingContext context, string text, double x, double y, IBrush brush, double size)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush) { MaxTextWidth = 510, Trimming = TextTrimming.CharacterEllipsis };
            context.DrawText(formatted, new Point(x, y));
        }
    }
}
