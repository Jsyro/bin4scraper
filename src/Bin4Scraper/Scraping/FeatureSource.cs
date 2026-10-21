using System.Text;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Scraping;

public interface IFeatureSource
{
    Uri Url { get; }
    Task<string> FetchAsync(CancellationToken cancellationToken);
}

public sealed class HttpFeatureSource(HttpClient httpClient, IOptions<ScraperOptions> options) : IFeatureSource
{
    public Uri Url { get; } = new(options.Value.FeatureUrl);

    public async Task<string> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(Url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

public static class MessageFormatter
{
    public static string Format(string title, IEnumerable<FeatureItem> items) => ToSmsSafe(BuildMessage(title, items));

    /// <summary>Replaces typographic punctuation so the message stays in the GSM-7 alphabet (160-char segments instead of 70).</summary>
    public static string ToSmsSafe(string text) => text
        .Replace('\u2013', '-').Replace('\u2014', '-')
        .Replace('\u2018', '\'').Replace('\u2019', '\'')
        .Replace('\u201C', '"').Replace('\u201D', '"')
        .Replace("\u2026", "...").Replace('\u00A0', ' ');

    private static string BuildMessage(string title, IEnumerable<FeatureItem> items)
    {
        var sb = new StringBuilder("Bin 4 ").Append(title);
        foreach (var item in items)
        {
            sb.Append("\n\n").Append(item.Category).Append(": ").Append(item.Name);
            if (!string.IsNullOrWhiteSpace(item.Price))
            {
                sb.Append(" (").Append(FormatPrice(item.Price)).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                sb.Append('\n').Append(item.Description);
            }
        }
        return sb.ToString();
    }

    private static string FormatPrice(string price) =>
        char.IsDigit(price[0]) ? "$" + price : price;
}
