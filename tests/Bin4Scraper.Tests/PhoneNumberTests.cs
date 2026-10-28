using Bin4Scraper.Sms;

namespace Bin4Scraper.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("250-555-0100", "+12505550100")]
    [InlineData("(250) 555 0100", "+12505550100")]
    [InlineData("1 250 555 0100", "+12505550100")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public void Normalizes_to_e164(string input, string expected)
    {
        Assert.True(PhoneNumber.TryNormalize(input, "1", out var e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("not a number")]
    public void Rejects_invalid(string? input)
    {
        Assert.False(PhoneNumber.TryNormalize(input, "1", out _));
    }

    [Fact]
    public void Mask_hides_all_but_last_four()
    {
        var masked = PhoneNumber.Mask("+12505550100");
        Assert.EndsWith("0100", masked);
        Assert.DoesNotContain("250555", masked);
    }
}
