using System.Xml;
using System.Xml.Linq;

namespace SHOU.Launcher.Core;

public sealed record HomeAction(string Type, string Data);
public sealed record HomeElement(string Kind, string Text, string? Source, string? Link, bool Horizontal, double FontSize, IReadOnlyList<HomeElement> Children,
    IReadOnlyDictionary<string, string>? Properties = null, IReadOnlyList<HomeAction>? Actions = null);
public static class CustomHomeDocument
{
    private static readonly Dictionary<string, string> EventAliases = new(StringComparer.OrdinalIgnoreCase)
    { ["打开网页"] = "OpenUrl", ["复制文本"] = "CopyText", ["刷新主页"] = "RefreshHomepage", ["刷新页面"] = "RefreshPage", ["弹出提示"] = "ShowHint", ["弹出窗口"] = "ShowDialog", ["切换页面"] = "SwitchPage" };
    private static readonly string[] SupportedEvents = ["OpenUrl", "CopyText", "RefreshHomepage", "RefreshPage", "ShowHint", "ShowDialog", "SwitchPage"];
    public static HomeAction Action(string type, string data)
    {
        type = EventAliases.GetValueOrDefault(type, type);
        type = SupportedEvents.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("尚未适配的主页事件：" + type);
        if (type == "OpenUrl" && !IsWebLink(data)) throw new InvalidDataException("主页按钮需要不含凭据的 HTTP(S) 地址。");
        if (type == "ShowDialog" && data.Split('|').Length < 2) throw new InvalidDataException("弹出窗口参数须为：标题|正文[|按钮文字]。");
        return new(type, data);
    }
    public static IReadOnlyList<HomeElement> Parse(string text)
    {
        if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('<')) return [new("Text", text, null, null, false, 14, [])];
        text = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.StartsWith("<?xml", StringComparison.Ordinal)) { var end = text.IndexOf("?>", StringComparison.Ordinal); if (end < 0) throw new XmlException("XML 声明未结束。"); text = text[(end + 2)..]; }
        using var input = new StringReader("<Root xmlns:local=\"clr-namespace:PCL\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + text + "</Root>");
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 300000 });
        var document = XDocument.Load(reader); var budget = 512;
        HomeElement Read(XElement e, int depth)
        {
            if (--budget < 0 || depth > 32) throw new InvalidDataException("主页布局层级或组件过多。");
            string Attr(string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";
            var kind = e.Name.LocalName switch
            { "MyCard" => "Card", "Border" => "Border", "StackPanel" or "Grid" or "WrapPanel" => e.Name.LocalName, "TextBlock" or "MyHint" => "Text", "Image" => "Image", "MyButton" or "MyIconButton" or "Button" or "MyListItem" => "Button", _ => throw new InvalidDataException("尚不支持的主页组件：" + e.Name.LocalName) };
            if (e.Attributes().Any(a => a.Name.LocalName is "Click" or "Loaded" or "Class" or "Code")) throw new InvalidDataException("主页不加载 CLR 事件处理器。");
            var properties = e.Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name.LocalName, a => a.Value);
            var actions = new List<HomeAction>();
            if (Attr("EventType").Length > 0) actions.Add(Action(Attr("EventType"), Attr("EventData")));
            var children = new List<HomeElement>();
            foreach (var child in e.Elements())
            {
                var name = child.Name.LocalName;
                if (name.EndsWith(".RowDefinitions", StringComparison.Ordinal) || name.EndsWith(".ColumnDefinitions", StringComparison.Ordinal))
                {
                    var row = name.EndsWith(".RowDefinitions", StringComparison.Ordinal);
                    properties[row ? "RowDefinitions" : "ColumnDefinitions"] = string.Join(',', child.Elements().Select(d => (string?)d.Attribute(row ? "Height" : "Width") ?? "*"));
                }
                else if (name.EndsWith(".Events", StringComparison.Ordinal))
                {
                    foreach (var action in child.Descendants().Where(a => a.Name.LocalName == "CustomEvent")) actions.Add(Action((string?)action.Attribute("Type") ?? "", (string?)action.Attribute("Data") ?? ""));
                }
                else if (name.EndsWith(".Child", StringComparison.Ordinal) || name.EndsWith(".Children", StringComparison.Ordinal) || name.EndsWith(".Content", StringComparison.Ordinal)) children.AddRange(child.Elements().Select(c => Read(c, depth + 1)));
                else children.Add(Read(child, depth + 1));
            }
            var content = Attr("Text"); if (content.Length == 0) content = Attr(kind == "Card" ? "Title" : "Content");
            if (content.Length == 0 && !e.HasElements) content = e.Value;
            if (content.StartsWith("{DynamicResource ", StringComparison.Ordinal)) content = "";
            var size = double.TryParse(Attr("FontSize"), System.Globalization.CultureInfo.InvariantCulture, out var font) && double.IsFinite(font) ? Math.Clamp(font, 8, 72) : 14;
            return new(kind, content, Attr("Source"), actions.FirstOrDefault(a => a.Type == "OpenUrl")?.Data, Attr("Orientation") == "Horizontal", size, children, properties, actions);
        }
        return document.Root!.Elements().Select(e => Read(e, 0)).ToArray();
    }
    public static bool IsWebLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
