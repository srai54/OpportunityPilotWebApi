using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using OpportunityPilot.Application.Research;

namespace OpportunityPilot.Infrastructure.Research;

/// <summary>
/// HTML to bounded visible text (AngleSharp, scripting off) and RSS 2.0 / Atom parsing (System.Xml with DTDs
/// prohibited and no resolver, so no XXE or entity expansion).
/// </summary>
public sealed partial class ContentParser : IContentParser
{
    public const int MaxFeedEntries = 100;
    public const int MaxTextLength = 100_000;
    public const int MaxLinks = 10;

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "nav", "footer", "header", "svg", "template", "iframe", "object", "embed", "canvas", "head"
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "aside", "li", "ul", "ol", "dl", "dt", "dd", "tr", "td", "th", "table",
        "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "address", "figure", "figcaption", "form", "fieldset", "hr"
    };

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace ContentNs = "http://purl.org/rss/1.0/modules/content/";

    public PageText ParseHtml(string html, string? baseUrl)
    {
        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(html ?? string.Empty);
        var title = Collapse(document.Title ?? string.Empty);

        // Links are read before page chrome is dropped: contact and careers links usually sit in the footer.
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var root);
        var links = document.QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href")?.Trim())
            .Where(h => !string.IsNullOrEmpty(h))
            .Select(h => Resolve(root, h!))
            .Where(h => h is not null && ContactLink().IsMatch(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxLinks)
            .Select(h => h!)
            .ToList();

        var text = new StringBuilder();
        Walk((INode?)document.Body ?? document.DocumentElement, text);
        return new PageText(title.Length > 0 ? title : null, Bound(Collapse(text.ToString())), links);
    }

    public FeedDocument ParseFeed(string xml)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4_000_000,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var reader = XmlReader.Create(new StringReader(xml ?? string.Empty), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return new FeedDocument(false, null, [], "The feed is not well-formed XML, or it declares a DTD (not allowed).");
        }

        var root = document.Root;
        if (root is null) return new FeedDocument(false, null, [], "The feed is empty.");

        if (root.Name.LocalName == "rss")
        {
            var channel = root.Element("channel");
            var entries = (channel?.Elements("item") ?? [])
                .Take(MaxFeedEntries)
                .Select(item =>
                {
                    var link = HttpLink(item.Element("link")?.Value) ?? HttpLink(item.Element("guid")?.Value);
                    var body = item.Element(ContentNs + "encoded")?.Value ?? item.Element("description")?.Value;
                    return new FeedEntry(TitleOr(item.Element("title")?.Value, link), link, HtmlToText(body), Date(item.Element("pubDate")?.Value));
                })
                .ToList();
            return new FeedDocument(true, Clean(channel?.Element("title")?.Value), entries, null);
        }

        if (root.Name == Atom + "feed")
        {
            var entries = root.Elements(Atom + "entry")
                .Take(MaxFeedEntries)
                .Select(entry =>
                {
                    var link = HttpLink(entry.Elements(Atom + "link")
                        .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value);
                    var body = entry.Element(Atom + "content")?.Value ?? entry.Element(Atom + "summary")?.Value;
                    var date = Date(entry.Element(Atom + "published")?.Value) ?? Date(entry.Element(Atom + "updated")?.Value);
                    return new FeedEntry(TitleOr(entry.Element(Atom + "title")?.Value, link), link, HtmlToText(body), date);
                })
                .ToList();
            return new FeedDocument(true, Clean(root.Element(Atom + "title")?.Value), entries, null);
        }

        return new FeedDocument(false, null, [], "This is not an RSS 2.0 or Atom feed.");
    }

    /// <summary>Visible text of an HTML fragment (feed descriptions are usually escaped HTML).</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        if (!html.Contains('<')) return Bound(Collapse(System.Net.WebUtility.HtmlDecode(html)));
        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument($"<body>{html}</body>");
        var text = new StringBuilder();
        Walk((INode?)document.Body ?? document.DocumentElement, text);
        return Bound(Collapse(text.ToString()));
    }

    /// <summary>Spaces and tabs collapse to one space, lines are trimmed, runs of blank lines become one line break.</summary>
    public static string Collapse(string text)
    {
        var spaced = Spaces().Replace(text, " ");
        var lines = spaced.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }

    private static void Walk(INode? node, StringBuilder text)
    {
        if (node is null) return;
        foreach (var child in node.ChildNodes)
        {
            if (text.Length > MaxTextLength * 2) return;
            switch (child.NodeType)
            {
                case NodeType.Text:
                    text.Append(child.TextContent);
                    break;
                case NodeType.Element when child is IElement element:
                    if (Skipped.Contains(element.LocalName)) break;
                    if (element.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
                    {
                        text.Append('\n');
                        break;
                    }
                    var block = Blocks.Contains(element.LocalName);
                    if (block) text.Append('\n');
                    Walk(element, text);
                    if (block) text.Append('\n');
                    else text.Append(' ');
                    break;
            }
        }
    }

    private static string? Resolve(Uri? root, string href)
    {
        if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return href.Length <= 320 ? href : null;
        Uri? uri;
        // On Linux "/contact" parses as an absolute file:///contact, so only an http(s) address counts as absolute here.
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") uri = absolute;
        else if (root is not null && Uri.TryCreate(root, href, out var relative)) uri = relative;
        else return null;
        return uri.Scheme is "http" or "https" && uri.OriginalString.Length <= 1000 ? uri.ToString() : null;
    }

    private static string? HttpLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;

    private static string TitleOr(string? title, string? link) =>
        Clean(HtmlToText(title)) ?? link ?? "Untitled entry";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Collapse(value);

    private static DateTime? Date(string? value) =>
        DateTimeOffset.TryParse(value?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private static string Bound(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength];

    [GeneratedRegex(@"[\p{Zs}\t\r\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^mailto:|/(?:contact|contact-us|careers|jobs|partners?)(?:[/?#.]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContactLink();
}
