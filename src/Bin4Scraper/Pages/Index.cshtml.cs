using System.Text.Json;
using Bin4Scraper.Data;
using Bin4Scraper.Scraping;
using Bin4Scraper.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Pages;

public sealed class IndexModel(Store store, CheckService checks, IOptions<ScraperOptions> scraperOptions) : AppPageModel
{
    public ScraperOptions Scraper { get; } = scraperOptions.Value;
    public CheckRun? LatestCheck { get; private set; }
    public CheckRun? LatestSuccess { get; private set; }
    public IReadOnlyList<FeatureItem> LatestItems { get; private set; } = [];
    public IReadOnlyList<CheckRun> RecentChecks { get; private set; } = [];
    public int EnabledRecipients { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public FeaturePreview? Preview { get; private set; }
    public string? PreviewError { get; private set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostCheckAsync()
    {
        var check = await checks.RunAsync("manual", CancellationToken.None);
        if (check.Status == CheckStatus.Succeeded)
        {
            Flash(check.Changed
                ? $"Check succeeded: '{check.Title}' is new — notifications were queued for enabled recipients."
                : $"Check succeeded: '{check.Title}' is unchanged, so no messages were sent.");
        }
        else
        {
            Flash($"Check failed: {check.Error}", ok: false);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        try
        {
            Preview = await checks.PreviewAsync(HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PreviewError = ex.Message;
        }
        Load();
        return Page();
    }

    public async Task<IActionResult> OnPostSendLatestAsync()
    {
        var count = await checks.SendLatestToAllAsync(CancellationToken.None);
        Flash(count > 0
            ? $"Sent the latest feature to {count} recipient(s). See History for results."
            : "Nothing sent: there is no successful check yet, or no enabled recipients.", ok: count > 0);
        return RedirectToPage();
    }

    private void Load()
    {
        LatestCheck = store.GetLatestCheck();
        LatestSuccess = store.GetLatestSucceededCheck();
        LatestItems = LatestSuccess?.ItemsJson is { } json ? JsonSerializer.Deserialize<List<FeatureItem>>(json) ?? [] : [];
        RecentChecks = store.GetRecentChecks(5);
        EnabledRecipients = store.GetRecipients().Count(r => r.Enabled);
        ConsecutiveFailures = store.CountConsecutiveFailures();
    }
}
