using System.Xml;
using System.Xml.Linq;

namespace SHOU.Launcher.Core;

public sealed record HomeElement(string Kind, string Text, string? Source, string? Link, bool Horizontal, double FontSize, IReadOnlyList<HomeElement> Children);
public static class CustomHomeDocument
{
    public static IReadOnlyList<HomeElement> Parse(string text)
    {
        if (!text.TrimStart().StartsWith('<')) return [new("Text", text, null, null, false, 14, [])];
        if (text.StartsWith("<?xml", StringComparison.Ordinal)) text = text[(text.IndexOf("?>", StringComparison.Ordinal) + 2)..];
        using var input = new StringReader("<Root xmlns:local=\"clr-namespace:PCL\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + text + "</Root>");
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 300000 });
        var document = XDocument.Load(reader); var budget = 512;
        HomeElement Read(XElement e, int depth)
        {
            if (--budget < 0 || depth > 32) throw new InvalidDataException("主页布局层级或组件过多。");
            string Attr(string name) => (string?)e.Attribute(name) ?? "";
            var kind = e.Name.LocalName switch
            { "MyCard" or "Border" => "Card", "StackPanel" or "Grid" or "WrapPanel" => e.Name.LocalName, "TextBlock" or "MyHint" => "Text", "Image" => "Image", "MyButton" or "Button" or "MyListItem" => "Button", _ => throw new InvalidDataException("尚不支持的主页组件：" + e.Name.LocalName) };
            var type = Attr("EventType"); var link = Attr("EventData");
            if (kind == "Button" && type is not ("OpenUrl" or "打开网页") || e.Attributes().Any(a => a.Name.LocalName is "Click" or "Class" or "Code")) throw new InvalidDataException("主页只支持打开网页按钮；此事件尚未适配：" + type);
            if (kind == "Button" && !IsWebLink(link)) throw new InvalidDataException("主页按钮需要不含凭据的 HTTP(S) 地址。");
            var content = Attr("Text"); if (content.Length == 0) content = Attr(kind == "Card" ? "Title" : "Content");
            if (content.Length == 0 && !e.HasElements) content = e.Value;
            if (content.StartsWith("{DynamicResource ", StringComparison.Ordinal)) content = "";
            var size = double.TryParse(Attr("FontSize"), System.Globalization.CultureInfo.InvariantCulture, out var font) && double.IsFinite(font) ? Math.Clamp(font, 8, 72) : 14;
            return new(kind, content, Attr("Source"), kind == "Button" ? link : null, Attr("Orientation") == "Horizontal", size, e.Elements().Select(c => Read(c, depth + 1)).ToArray());
        }
        return document.Root!.Elements().Select(e => Read(e, 0)).ToArray();
    }
    public static bool IsWebLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
