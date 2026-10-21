using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Bin4Scraper.Scraping;

public sealed record FeatureItem(string Category, string Name, string? Price, string? Description, IReadOnlyList<string> Notes);

public sealed record FeatureMenu(string Title, IReadOnlyList<FeatureItem> Items)
{
    public IReadOnlyList<FeatureItem> Select(IReadOnlyList<string> categories)
    {
        if (categories.Count == 0 || categories.Contains("*"))
        {
            return Items;
        }
        return Items.Where(i => categories.Contains(i.Category, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Stable identity of a feature set; changes when the month or selected item names change.</summary>
    public static string Fingerprint(string title, IEnumerable<FeatureItem> items)
    {
        var canonical = new StringBuilder(Normalize(title));
        foreach (var item in items.OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            canonical.Append('\n').Append(Normalize(item.Category)).Append('|').Append(Normalize(item.Name));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16].ToLowerInvariant();
    }

    private static string Normalize(string value) =>
        FeatureParser.CollapseWhitespace(value).ToLowerInvariant();
}

public sealed class FeatureParseException(string message) : Exception(message);

/// <summary>
/// Parses the Bin 4 "Monthly Features" page. The page has an h2 such as "October Features", followed by
/// h6 category headings (Appetizer, Burger, ...) each followed by one or more <c>.xd-menu-item</c> blocks.
/// </summary>
public static partial class FeatureParser
{
    public static FeatureMenu Parse(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var root = (IParentNode?)document.QuerySelector("main") ?? document;

        var titleElement = root.QuerySelectorAll("h1, h2, h3")
            .FirstOrDefault(h => FeaturesTitle().IsMatch(CollapseWhitespace(h.TextContent)) && !IsMonthlyFeaturesHero(h))
            ?? throw new FeatureParseException("Could not find a heading like 'October Features' on the page.");

        var title = CollapseWhitespace(titleElement.TextContent);
        var scope = titleElement.Closest("section") ?? titleElement.ParentElement ?? throw new FeatureParseException("Feature heading has no container.");

        var items = new List<FeatureItem>();
        string? category = null;
        foreach (var element in scope.QuerySelectorAll("h6, .xd-menu-item"))
        {
            if (element.LocalName == "h6")
            {
                category = CollapseWhitespace(element.TextContent);
                continue;
            }
            if (category is null)
            {
                continue; // Introductory text block before the first category.
            }
            items.AddRange(ParseMenuItem(category, element));
        }

        if (items.Count == 0)
        {
            throw new FeatureParseException($"Found '{title}' but no feature items beneath it.");
        }
        return new FeatureMenu(title, items);
    }

    private static bool IsMonthlyFeaturesHero(IElement heading) =>
        heading.Closest(".hero") is not null;

    private static IEnumerable<FeatureItem> ParseMenuItem(string category, IElement block)
    {
        var spans = block.QuerySelector(".xd-menu-item__heading")?.QuerySelectorAll("span").ToList() ?? [];
        var name = spans.Count > 0 ? CollapseWhitespace(spans[0].TextContent) : "";
        var price = spans.Count > 1 ? CollapseWhitespace(spans[1].TextContent) : "";
        var paragraphs = block.QuerySelectorAll("p")
            .Where(p => !p.ClassList.Contains("xd-menu-item__heading"))
            .ToList();

        if (name.Length > 0)
        {
            var texts = paragraphs.Select(p => CollapseWhitespace(p.TextContent)).Where(t => t.Length > 0).ToList();
            yield return new FeatureItem(category, name, NullIfEmpty(price), texts.FirstOrDefault(), texts.Skip(1).ToList());
            yield break;
        }

        // Some blocks (e.g. desserts) list several items as alternating bold "Name  price" and description paragraphs.
        string? currentName = null, currentPrice = null, currentDescription = null;
        var notes = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            var text = CollapseWhitespace(paragraph.TextContent);
            if (text.Length == 0)
            {
                continue;
            }
            if (IsEntirelyBold(paragraph))
            {
                if (currentName is not null)
                {
                    yield return new FeatureItem(category, currentName, currentPrice, currentDescription, notes);
                }
                var match = TrailingPrice().Match(text);
                (currentName, currentPrice) = match.Success ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()) : (text, null);
                currentDescription = null;
                notes = [];
            }
            else if (currentName is not null && currentDescription is null)
            {
                currentDescription = text;
            }
            else if (currentName is not null)
            {
                notes.Add(text);
            }
        }
        if (currentName is not null)
        {
            yield return new FeatureItem(category, currentName, currentPrice, currentDescription, notes);
        }
    }

    private static bool IsEntirelyBold(IElement paragraph)
    {
        var boldText = string.Concat(paragraph.QuerySelectorAll("strong, b")
            .Where(e => e.ParentElement?.Closest("strong, b") is null)
            .Select(e => e.TextContent));
        return CollapseWhitespace(boldText) == CollapseWhitespace(paragraph.TextContent);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    public static string CollapseWhitespace(string value) => Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bfeatures?\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturesTitle();

    [GeneratedRegex(@"^(.*?)\s+\$?(\d+(?:\.\d{1,2})?(?:\s*\([^)]*\))?)$")]
    private static partial Regex TrailingPrice();
}
