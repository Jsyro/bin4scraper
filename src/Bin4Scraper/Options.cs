namespace Bin4Scraper;

public sealed class ScraperOptions
{
    public const string Section = "Scraper";

    public bool Enabled { get; set; } = true;
    public string FeatureUrl { get; set; } = "https://bin4burgerlounge.com/monthly-features/";

    /// <summary>Comma-separated feature categories to notify about, e.g. "Burger" or "Burger,Appetizer". Use "*" for all.</summary>
    public string Categories { get; set; } = "Burger";

    /// <summary>Five-field cron expression evaluated in <see cref="TimeZone"/>.</summary>
    public string Schedule { get; set; } = "0 9 * * *";
    public string TimeZone { get; set; } = "America/Vancouver";
    public bool NotifyOnFirstRun { get; set; } = true;
    public int RetryDelayMinutes { get; set; } = 60;
    public int SnapshotRetention { get; set; } = 30;
    public string UserAgent { get; set; } = "Bin4Scraper/2.0 (+https://github.com/Jsyro/bin4scraper)";

    public IReadOnlyList<string> CategoryList =>
        Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string DataDirectory { get; set; } = "data";
}

public sealed class SmsOptions
{
    public const string Section = "Sms";

    /// <summary>Console (log only), Textbelt, or Twilio.</summary>
    public string Provider { get; set; } = "Console";
    public string DefaultCountryCode { get; set; } = "1";
    public int MaxAttempts { get; set; } = 3;
    public int DelayBetweenMessagesMs { get; set; } = 1000;

    /// <summary>Optional phone number that receives an alert after repeated check failures.</summary>
    public string? AdminPhone { get; set; }
    public int FailureAlertThreshold { get; set; } = 3;

    public TextbeltOptions Textbelt { get; set; } = new();
    public TwilioOptions Twilio { get; set; } = new();
}

public sealed class TextbeltOptions
{
    public string Url { get; set; } = "https://textbelt.com/text";
    public string? Key { get; set; }

    /// <summary>Appends "_test" to the key so Textbelt validates the request without sending or using quota.</summary>
    public bool TestMode { get; set; }
}

public sealed class TwilioOptions
{
    public string? AccountSid { get; set; }
    public string? AuthToken { get; set; }
    public string? From { get; set; }
}

public sealed class UiOptions
{
    public const string Section = "Ui";

    public string Username { get; set; } = "admin";

    /// <summary>When set, the web UI requires HTTP basic authentication.</summary>
    public string? Password { get; set; }
}
