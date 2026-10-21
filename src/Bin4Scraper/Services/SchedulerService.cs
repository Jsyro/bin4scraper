using Bin4Scraper.Data;
using Cronos;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Services;

/// <summary>Parsed schedule configuration plus the scheduler's current state for display.</summary>
public sealed class Schedule
{
    public Schedule(IOptions<ScraperOptions> options)
    {
        var value = options.Value;
        Enabled = value.Enabled;
        Expression = value.Schedule;
        Cron = CronExpression.Parse(value.Schedule);
        Zone = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZone);
        RetryDelay = TimeSpan.FromMinutes(Math.Max(1, value.RetryDelayMinutes));
    }

    public bool Enabled { get; }
    public string Expression { get; }
    public CronExpression Cron { get; }
    public TimeZoneInfo Zone { get; }
    public TimeSpan RetryDelay { get; }
    public DateTimeOffset? NextCheckAt { get; set; }

    public DateTimeOffset? Next(DateTimeOffset from) => Cron.GetNextOccurrence(from, Zone);

    public DateTimeOffset? Previous(DateTimeOffset now) =>
        Cron.GetOccurrences(now.AddDays(-35), now, Zone, fromInclusive: true, toInclusive: true)
            .Select(o => (DateTimeOffset?)o)
            .LastOrDefault();

    public DateTimeOffset ToLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);

    public string Format(DateTimeOffset? value) =>
        value is { } v ? ToLocal(v).ToString("yyyy-MM-dd HH:mm") : "—";
}

public sealed class SchedulerService(
    Schedule schedule,
    CheckService checks,
    DeliveryService deliveries,
    Store store,
    TimeProvider time,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var next = InitialNextCheck();
        schedule.NextCheckAt = next;
        if (!schedule.Enabled)
        {
            logger.LogWarning("Scheduled checks are disabled (Scraper:Enabled=false). Manual checks and deliveries still work.");
        }
        else
        {
            logger.LogInformation("Schedule '{Expression}' ({Zone}); next check at {Next}", schedule.Expression, schedule.Zone.Id, next);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await deliveries.DispatchPendingAsync(stoppingToken);
                var now = time.GetUtcNow();
                if (next is { } due && now >= due)
                {
                    var check = await checks.RunAsync("schedule", stoppingToken);
                    var after = time.GetUtcNow();
                    var cronNext = schedule.Next(after);
                    next = check.Status == CheckStatus.Failed && (cronNext is null || after + schedule.RetryDelay < cronNext)
                        ? after + schedule.RetryDelay
                        : cronNext;
                    schedule.NextCheckAt = next;
                    logger.LogInformation("Next check at {Next}", next);
                    continue;
                }

                var wait = DispatchInterval;
                if (next is { } upcoming && upcoming - now < wait)
                {
                    wait = upcoming - now;
                }
                await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler loop error");
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            }
        }
    }

    /// <summary>Runs immediately if a scheduled check was missed while the app was down; otherwise waits for the next occurrence.</summary>
    private DateTimeOffset? InitialNextCheck()
    {
        if (!schedule.Enabled)
        {
            return null;
        }
        var now = time.GetUtcNow();
        var previous = schedule.Previous(now);
        var lastSuccess = store.GetLatestSucceededCheck();
        if (previous is not null && (lastSuccess is null || lastSuccess.StartedAt < previous))
        {
            logger.LogInformation("Missed scheduled check at {Previous}; running a catch-up check now", previous);
            return now;
        }
        return schedule.Next(now);
    }
}
