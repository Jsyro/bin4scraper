using Bin4Scraper.Scraping;

namespace Bin4Scraper.Tests;

public class FeatureParserTests
{
    private static FeatureMenu ParseFixture() =>
        FeatureParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "monthly-features.html")));

    [Fact]
    public void Parses_title_outside_hero()
    {
        Assert.Equal("November Features", ParseFixture().Title);
    }

    [Fact]
    public void Parses_burger_with_split_price_and_collapsed_whitespace()
    {
        var burger = Assert.Single(ParseFixture().Select(["Burger"]));
        Assert.Equal("Bin 999 \u2013 THE TEST BURGER", burger.Name);
        Assert.Equal("26.75", burger.Price);
        Assert.Equal("Premium BC beef, pickles, special sauce", burger.Description);
    }

    [Fact]
    public void Parses_multiple_desserts_from_bold_paragraphs()
    {
        var desserts = ParseFixture().Select(["Dessert"]);
        Assert.Collection(desserts,
            d => { Assert.Equal("Apple Crumble", d.Name); Assert.Equal("8.25", d.Price); Assert.Equal("Warm apples, oat topping.", d.Description); },
            d => { Assert.Equal("Lemon Tart", d.Name); Assert.Equal("7", d.Price); });
    }

    [Fact]
    public void Select_is_case_insensitive_and_star_means_all()
    {
        var menu = ParseFixture();
        Assert.Single(menu.Select(["burger"]));
        Assert.Contains(menu.Items, i => i.Category == "Appetizer");
        Assert.Equal(menu.Items.Count, menu.Select(["*"]).Count);
    }

    [Fact]
    public void Throws_when_page_structure_is_unrecognised()
    {
        Assert.Throws<FeatureParseException>(() => FeatureParser.Parse("<html><body><h1>Nothing here</h1></body></html>"));
    }

    [Fact]
    public void Fingerprint_ignores_price_and_description_but_not_name()
    {
        var a = new FeatureItem("Burger", "Bin 1", "20", "desc", []);
        var priceChange = a with { Price = "21", Description = "other" };
        var nameChange = a with { Name = "Bin 2" };

        Assert.Equal(FeatureMenu.Fingerprint("Oct Features", [a]), FeatureMenu.Fingerprint("Oct Features", [priceChange]));
        Assert.NotEqual(FeatureMenu.Fingerprint("Oct Features", [a]), FeatureMenu.Fingerprint("Oct Features", [nameChange]));
        Assert.NotEqual(FeatureMenu.Fingerprint("Oct Features", [a]), FeatureMenu.Fingerprint("Nov Features", [a]));
    }

    [Fact]
    public void Formatter_produces_gsm_safe_text()
    {
        var text = MessageFormatter.Format("November Features", [new FeatureItem("Burger", "Bin 9 \u2013 \u201CBig\u201D", "25", "Beef", [])]);
        Assert.Contains("Burger: Bin 9 - \"Big\" ($25)", text);
        Assert.All(text, ch => Assert.True(ch < 128, $"Non-ASCII character U+{(int)ch:X4}"));
    }
}
