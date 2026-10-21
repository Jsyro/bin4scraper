using System.Diagnostics.CodeAnalysis;

namespace Bin4Scraper.Sms;

public static class PhoneNumber
{
    /// <summary>Normalizes user input to E.164 (e.g. "250-555-1234" → "+12505551234").</summary>
    public static bool TryNormalize(string? input, string defaultCountryCode, [NotNullWhen(true)] out string? e164)
    {
        e164 = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        var trimmed = input.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        var countryCode = new string(defaultCountryCode.Where(char.IsDigit).ToArray());

        if (trimmed.StartsWith('+'))
        {
            if (digits.Length is < 8 or > 15) return false;
            e164 = "+" + digits;
        }
        else if (countryCode == "1" && digits.Length == 11 && digits[0] == '1')
        {
            e164 = "+" + digits;
        }
        else if (countryCode == "1" && digits.Length == 10)
        {
            e164 = "+1" + digits;
        }
        else if (countryCode != "1" && digits.Length is >= 6 and <= 14)
        {
            e164 = "+" + countryCode + digits.TrimStart('0');
        }
        return e164 is not null;
    }

    /// <summary>Masks a number for logs and display, keeping only the last four digits.</summary>
    public static string Mask(string phone) =>
        phone.Length <= 4 ? "****" : new string('•', Math.Min(phone.Length - 4, 7)) + phone[^4..];
}
