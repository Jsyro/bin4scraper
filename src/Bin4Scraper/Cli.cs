using Bin4Scraper.Data;
using Bin4Scraper.Services;
using Bin4Scraper.Sms;
using Microsoft.Extensions.Options;

namespace Bin4Scraper;

/// <summary>Command-line entry points for testing scraping and SMS delivery without the web UI.</summary>
public static class Cli
{
    private const string Usage = """
        Usage: Bin4Scraper [command] [options] [--Section:Key=value ...]

        Commands (omit to start the web app):
          check                          Fetch and parse the feature page; print the SMS text. Saves and sends nothing.
          check --notify                 Run a real check: record it and text recipients if the feature changed.
          send-test --to <phone>         Send a test SMS using the configured provider.
                    [--message <text>]   Custom message text.
                    [--feature]          Send the live feature message instead of a test message.
          healthcheck                    Exit 0 if the running web app answers /healthz.

        Examples:
          dotnet run --project src/Bin4Scraper -- send-test --to 2505551234
          dotnet run --project src/Bin4Scraper -- send-test --to 2505551234 --Sms:Provider=Textbelt --Sms:Textbelt:TestMode=true
          docker exec bin4scraper dotnet Bin4Scraper.dll check
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var options = ParseOptions(args.Skip(1));
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "check" => await CheckAsync(services, options.ContainsKey("notify")),
                "send-test" => await SendTestAsync(services, options),
                "help" or "--help" or "-h" => PrintUsage(0),
                _ => PrintUsage(1, $"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> HealthCheckAsync()
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', ',')[0].Trim();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> CheckAsync(IServiceProvider services, bool notify)
    {
        var checks = services.GetRequiredService<CheckService>();
        if (!notify)
        {
            var preview = await checks.PreviewAsync(CancellationToken.None);
            Console.WriteLine($"Found '{preview.Menu.Title}' with {preview.Menu.Items.Count} item(s):");
            foreach (var item in preview.Menu.Items)
            {
                Console.WriteLine($"  [{item.Category}] {item.Name} {item.Price}");
            }
            Console.WriteLine($"\nFingerprint: {preview.Fingerprint}\nSMS text ({preview.Message.Length} chars):\n---\n{preview.Message}\n---");
            return 0;
        }

        var check = await checks.RunAsync("cli", CancellationToken.None);
        Console.WriteLine($"Check {check.Id}: {check.Status}{(check.Error is null ? "" : " — " + check.Error)}");
        Console.WriteLine($"Changed: {check.Changed}");
        return check.Status == CheckStatus.Succeeded ? 0 : 1;
    }

    private static async Task<int> SendTestAsync(IServiceProvider services, Dictionary<string, string?> options)
    {
        var sms = services.GetRequiredService<IOptions<SmsOptions>>().Value;
        if (!PhoneNumber.TryNormalize(options.GetValueOrDefault("to"), sms.DefaultCountryCode, out var phone))
        {
            return PrintUsage(1, "send-test requires a valid --to <phone>.");
        }

        var message = options.GetValueOrDefault("message");
        if (options.ContainsKey("feature"))
        {
            message = (await services.GetRequiredService<CheckService>().PreviewAsync(CancellationToken.None)).Message;
        }
        message ??= $"Bin4Scraper test message from {Environment.MachineName} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}.";

        var deliveries = services.GetRequiredService<DeliveryService>();
        Console.WriteLine($"Sending via {deliveries.Sender.Description} to {phone}...");
        var delivery = await deliveries.SendNowAsync(phone, message, DeliveryKind.Test, null, CancellationToken.None);
        Console.WriteLine($"Status: {delivery.Status}");
        if (delivery.ProviderMessageId is not null) Console.WriteLine($"Provider message id: {delivery.ProviderMessageId}");
        if (delivery.Error is not null) Console.WriteLine($"Details: {delivery.Error}");
        return delivery.Status == DeliveryStatus.Sent ? 0 : 1;
    }

    private static Dictionary<string, string?> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--") && arg.Contains('='))
            {
                pending = null; // Configuration override, handled by the host builder.
            }
            else if (arg.StartsWith("--"))
            {
                pending = arg[2..];
                result[pending] = null;
            }
            else if (pending is not null)
            {
                result[pending] = arg;
                pending = null;
            }
        }
        return result;
    }

    private static int PrintUsage(int exitCode, string? error = null)
    {
        if (error is not null) Console.Error.WriteLine(error + "\n");
        Console.WriteLine(Usage);
        return exitCode;
    }
}
