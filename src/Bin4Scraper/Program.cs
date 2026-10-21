using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Bin4Scraper;
using Bin4Scraper.Data;
using Bin4Scraper.Scraping;
using Bin4Scraper.Services;
using Bin4Scraper.Sms;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

if (args is ["healthcheck", ..])
{
    return await Cli.HealthCheckAsync();
}

var cliCommand = args is [var first, ..] && !first.StartsWith('-') ? first : null;

// In CLI mode only "--Section:Key=value" overrides reach configuration; command flags are parsed by Cli.
var builder = WebApplication.CreateBuilder(cliCommand is null ? args : args.Where(a => a.StartsWith("--") && a.Contains('=')).ToArray());
var services = builder.Services;

services.Configure<ScraperOptions>(builder.Configuration.GetSection(ScraperOptions.Section));
services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
services.Configure<SmsOptions>(builder.Configuration.GetSection(SmsOptions.Section));
services.Configure<UiOptions>(builder.Configuration.GetSection(UiOptions.Section));

services.AddSingleton(TimeProvider.System);
services.AddSingleton<Store>();
services.AddSingleton<Schedule>();
services.AddSingleton<CheckService>();
services.AddSingleton<DeliveryService>();
services.AddHostedService<SchedulerService>();

services.AddHttpClient<IFeatureSource, HttpFeatureSource>((sp, client) =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(sp.GetRequiredService<IOptions<ScraperOptions>>().Value.UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
    })
    .AddStandardResilienceHandler();

// SMS calls are not idempotent, so they get a plain timeout and no automatic HTTP retries.
services.AddHttpClient<TextbeltSmsSender>(c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddHttpClient<TwilioSmsSender>(c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddSingleton<ConsoleSmsSender>();
services.AddSingleton<ISmsSender>(sp =>
{
    var provider = sp.GetRequiredService<IOptions<SmsOptions>>().Value.Provider;
    return provider.ToLowerInvariant() switch
    {
        "console" => sp.GetRequiredService<ConsoleSmsSender>(),
        "textbelt" => sp.GetRequiredService<TextbeltSmsSender>(),
        "twilio" => sp.GetRequiredService<TwilioSmsSender>(),
        _ => throw new InvalidOperationException($"Unknown Sms:Provider '{provider}'. Use Console, Textbelt, or Twilio."),
    };
});

var dataDirectory = Path.GetFullPath(builder.Configuration[$"{StorageOptions.Section}:DataDirectory"] ?? new StorageOptions().DataDirectory);
services.AddDataProtection()
    .SetApplicationName("Bin4Scraper")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
services.AddRazorPages();

var app = builder.Build();

app.Services.GetRequiredService<Store>().Initialize();
_ = app.Services.GetRequiredService<Schedule>(); // Fail fast on an invalid cron expression or time zone.
_ = app.Services.GetRequiredService<ISmsSender>(); // Fail fast on an unknown provider.

if (cliCommand is not null)
{
    return await Cli.RunAsync(app.Services, args);
}

var ui = app.Services.GetRequiredService<IOptions<UiOptions>>().Value;
if (!string.IsNullOrEmpty(ui.Password))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/healthz") || IsAuthorized(context.Request, ui))
        {
            await next();
            return;
        }
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Bin4Scraper\", charset=\"UTF-8\"";
    });
}

app.UseStaticFiles();
app.MapRazorPages();

app.MapGet("/healthz", (Store store) =>
{
    var latest = store.GetLatestCheck();
    var lastSuccess = store.GetLatestSucceededCheck();
    return Results.Ok(new
    {
        status = "ok",
        lastCheckStatus = latest?.Status,
        lastCheckAt = latest?.StartedAt,
        lastSuccessAt = lastSuccess?.StartedAt,
        consecutiveFailures = store.CountConsecutiveFailures(),
    });
});

app.MapGet("/backup", (Store store, TimeProvider time) =>
{
    var path = Path.Combine(Path.GetTempPath(), $"bin4scraper-backup-{Guid.NewGuid():N}.db");
    try
    {
        store.BackupTo(path);
        return Results.File(File.ReadAllBytes(path), "application/vnd.sqlite3", $"bin4scraper-{time.GetUtcNow():yyyyMMdd-HHmmss}.db");
    }
    finally
    {
        File.Delete(path);
    }
});

app.Run();
return 0;

static bool IsAuthorized(HttpRequest request, UiOptions ui)
{
    if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
        || !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
        || header.Parameter is null)
    {
        return false;
    }
    try
    {
        var expected = Encoding.UTF8.GetBytes($"{ui.Username}:{ui.Password}");
        var actual = Convert.FromBase64String(header.Parameter);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
    catch (FormatException)
    {
        return false;
    }
}
