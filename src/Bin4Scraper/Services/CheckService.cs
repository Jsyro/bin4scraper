using System.Text.Json;
using Bin4Scraper.Data;
using Bin4Scraper.Scraping;
using Bin4Scraper.Sms;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Services;

public sealed record FeaturePreview(FeatureMenu Menu, IReadOnlyList<FeatureItem> Selected, string Message, string Fingerprint);

/// <summary>Fetches and parses the feature page, records the result, and queues notifications when the feature changes.</summary>
public sealed class CheckService(
    IFeatureSource source,
    Store store,
    DeliveryService deliveries,
    IOptions<ScraperOptions> scraperOptions,
    IOptions<SmsOptions> smsOptions,
    TimeProvider time,
    ILogger<CheckService> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ScraperOptions _scraper = scraperOptions.Value;

    /// <summary>Fetches and parses the page without saving anything or sending messages.</summary>
    public async Task<FeaturePreview> PreviewAsync(CancellationToken cancellationToken) =>
        Build(await source.FetchAsync(cancellationToken));

    public async Task<CheckRun> RunAsync(string triggeredBy, CancellationToken cancellationToken)
    {
        CheckRun check;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var startedAt = time.GetUtcNow();
            check = new CheckRun
            {
                Id = store.StartCheck(triggeredBy, startedAt),
                StartedAt = startedAt,
                TriggeredBy = triggeredBy,
            };

            try
            {
                var html = await source.FetchAsync(cancellationToken);
                check.SnapshotFile = SaveSnapshot(check, html);
                var preview = Build(html);

                check.Title = preview.Menu.Title;
                check.Fingerprint = preview.Fingerprint;
                check.ItemsJson = JsonSerializer.Serialize(preview.Selected);
                check.Message = preview.Message;
                check.Status = CheckStatus.Succeeded;
                check.CompletedAt = time.GetUtcNow();

                var queued = CreateFeatureDeliveries(check.Id, preview.Message);
                var queuedCount = store.CompleteSuccessfulCheck(check, _scraper.NotifyOnFirstRun, queued);
                logger.LogInformation("Check {Id} succeeded: '{Title}' ({Fingerprint}), changed={Changed}, queued {Count} message(s)",
                    check.Id, check.Title, check.Fingerprint, check.Changed, queuedCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                check.Status = CheckStatus.Failed;
                check.Error = ex is FeatureParseException or HttpRequestException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                check.CompletedAt = time.GetUtcNow();
                store.CompleteCheck(check, CreateFailureAlert(check.Error));
                logger.LogError(ex, "Check {Id} failed", check.Id);
            }
        }
        finally
        {
            _lock.Release();
        }

        await deliveries.DispatchPendingAsync(cancellationToken);
        return check;
    }

    /// <summary>Queues the most recent successfully scraped feature for every enabled recipient.</summary>
    public async Task<int> SendLatestToAllAsync(CancellationToken cancellationToken)
    {
        var latest = store.GetLatestSucceededCheck();
        if (latest?.Message is null)
        {
            return 0;
        }
        var queued = CreateFeatureDeliveries(latest.Id, latest.Message);
        foreach (var delivery in queued)
        {
            store.AddDelivery(delivery);
        }
        await deliveries.DispatchPendingAsync(cancellationToken);
        return queued.Count;
    }

    private FeaturePreview Build(string html)
    {
        var menu = FeatureParser.Parse(html);
        var categories = _scraper.CategoryList;
        var selected = menu.Select(categories);
        if (selected.Count == 0)
        {
            throw new FeatureParseException(
                $"'{menu.Title}' has no items in categories [{string.Join(", ", categories)}]. " +
                $"Found categories: {string.Join(", ", menu.Items.Select(i => i.Category).Distinct())}.");
        }
        return new FeaturePreview(menu, selected, MessageFormatter.Format(menu.Title, selected), FeatureMenu.Fingerprint(menu.Title, selected));
    }

    private List<Delivery> CreateFeatureDeliveries(long checkId, string message)
    {
        var now = time.GetUtcNow();
        return store.GetRecipients()
            .Where(r => r.Enabled)
            .Select(r => new Delivery
            {
                CheckId = checkId,
                RecipientId = r.Id,
                Phone = r.Phone,
                Kind = DeliveryKind.Feature,
                Message = message,
                Status = DeliveryStatus.Pending,
                CreatedAt = now,
            })
            .ToList();
    }

    private List<Delivery> CreateFailureAlert(string error)
    {
        var sms = smsOptions.Value;
        // The current check is still "running" in the database, so add it to the count.
        var failures = store.CountConsecutiveFailures() + 1;
        if (failures != sms.FailureAlertThreshold || !PhoneNumber.TryNormalize(sms.AdminPhone, sms.DefaultCountryCode, out var adminPhone))
        {
            return [];
        }
        var detail = error.Length > 300 ? error[..300] + "…" : error;
        return
        [
            new Delivery
            {
                Phone = adminPhone,
                Kind = DeliveryKind.Alert,
                Message = $"Bin4Scraper: {failures} consecutive feature checks have failed. Latest error: {detail}",
                Status = DeliveryStatus.Pending,
                CreatedAt = time.GetUtcNow(),
            },
        ];
    }

    private string? SaveSnapshot(CheckRun check, string html)
    {
        try
        {
            var directory = Path.Combine(store.DataDirectory, "snapshots");
            Directory.CreateDirectory(directory);
            var fileName = $"{check.StartedAt:yyyyMMdd-HHmmss}-check{check.Id}.html";
            File.WriteAllText(Path.Combine(directory, fileName), html);

            foreach (var old in Directory.GetFiles(directory, "*.html").Order(StringComparer.Ordinal).SkipLast(Math.Max(1, _scraper.SnapshotRetention)))
            {
                File.Delete(old);
            }
            return fileName;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not save page snapshot");
            return null;
        }
    }
}
