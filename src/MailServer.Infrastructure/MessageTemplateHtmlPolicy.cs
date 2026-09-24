using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MailServer.Infrastructure;

/// <summary>退信模板允许的 XHTML 片段。拒绝脚本、远程资源、事件属性和危险 CSS。</summary>
public static class MessageTemplateHtmlPolicy
{
    private static readonly HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        "section", "div", "p", "h1", "h2", "h3", "strong", "b", "em", "i", "u",
        "pre", "code", "br", "ul", "ol", "li", "blockquote", "table", "thead",
        "tbody", "tr", "th", "td", "span", "a", "hr"
    };

    private static readonly HashSet<string> StyleProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-family", "font-size", "font-weight", "line-height", "color", "background",
        "background-color", "white-space", "border", "border-radius", "padding", "margin",
        "text-align", "width", "max-width"
    };

    private static readonly Regex StyleValue = new(
        "^[a-zA-Z0-9#.,%\\s-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void Validate(string html)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader($"<root>{html}</root>"), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 20_000,
                IgnoreComments = false
            });
            var root = XDocument.Load(reader).Root!;
            foreach (var node in root.DescendantNodes())
            {
                if (node is XText) continue;
                if (node is not XElement element || !Tags.Contains(element.Name.LocalName)
                    || element.Name.Namespace != XNamespace.None)
                    throw new FormatException("HTML 正文包含不允许的标签或节点。请使用页面提示的安全 XHTML 标签。");
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name.Namespace != XNamespace.None)
                        throw new FormatException("HTML 正文不允许命名空间属性。");
                    if (attribute.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                        ValidateStyle(attribute.Value);
                    else if (attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase)
                             && element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
                        ValidateHref(attribute.Value);
                    else
                        throw new FormatException("HTML 正文包含不允许的属性。仅链接地址和安全的行内样式可用。");
                }
            }
        }
        catch (XmlException exception)
        {
            throw new FormatException("HTML 正文必须是完整的 XHTML 片段；请闭合标签，并将 & 写成 &amp;。", exception);
        }
    }

    private static void ValidateStyle(string style)
    {
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 1 || !StyleProperties.Contains(declaration[..separator].Trim())
                || !StyleValue.IsMatch(declaration[(separator + 1)..].Trim()))
                throw new FormatException("HTML 正文包含不安全的行内样式。");
        }
    }

    private static void ValidateHref(string href)
    {
        if (href.Any(char.IsControl)
            || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "mailto"))
            throw new FormatException("HTML 链接仅支持 http、https 或 mailto 地址。");
    }
}
