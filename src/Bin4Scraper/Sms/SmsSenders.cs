using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Sms;

public enum SmsOutcome
{
    Sent,

    /// <summary>The provider definitively rejected or never received the request; safe to retry.</summary>
    Failed,

    /// <summary>The request may have been delivered (e.g. timeout); do not retry automatically.</summary>
    Unknown,
}

public sealed record SmsResult(SmsOutcome Outcome, string? MessageId = null, string? Error = null, string? Detail = null);

public interface ISmsSender
{
    string Name { get; }
    string Description { get; }
    Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken);
}

/// <summary>Logs messages instead of sending them. Default provider so a fresh install never sends real SMS.</summary>
public sealed class ConsoleSmsSender(ILogger<ConsoleSmsSender> logger) : ISmsSender
{
    public string Name => "Console";
    public string Description => "Console (dry run — messages are only written to the log)";

    public Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
    {
        logger.LogInformation("[DRY RUN] SMS to {Phone}:\n{Message}", PhoneNumber.Mask(phone), message);
        return Task.FromResult(new SmsResult(SmsOutcome.Sent, "dry-run-" + Guid.NewGuid().ToString("N")[..8]));
    }
}

public sealed class TextbeltSmsSender(HttpClient httpClient, IOptions<SmsOptions> options) : ISmsSender
{
    private readonly TextbeltOptions _options = options.Value.Textbelt;

    public string Name => "Textbelt";
    public string Description => _options.TestMode ? "Textbelt (TEST MODE — validated but not delivered)" : "Textbelt";

    public async Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Key))
        {
            return new SmsResult(SmsOutcome.Failed, Error: "Sms:Textbelt:Key is not configured.");
        }
        var key = _options.TestMode ? _options.Key + "_test" : _options.Key;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["phone"] = phone,
            ["message"] = message,
            ["key"] = key,
        });

        return await SmsHttp.SendAsync(async () =>
        {
            using var response = await httpClient.PostAsync(_options.Url, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                return new SmsResult(SmsOutcome.Unknown, Error: $"Textbelt HTTP {(int)response.StatusCode}");
            }
            var result = JsonSerializer.Deserialize<TextbeltResponse>(body);
            if (result is { Success: true })
            {
                return new SmsResult(SmsOutcome.Sent, result.TextId ?? (_options.TestMode ? "test-mode" : null),
                    Detail: result.QuotaRemaining is { } q ? $"Textbelt quota remaining: {q}" : null);
            }
            return new SmsResult(SmsOutcome.Failed, Error: result?.Error ?? $"Textbelt HTTP {(int)response.StatusCode}: {body}");
        });
    }

    private sealed class TextbeltResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("textId")] public string? TextId { get; set; }
        [JsonPropertyName("quotaRemaining")] public int? QuotaRemaining { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}

public sealed class TwilioSmsSender(HttpClient httpClient, IOptions<SmsOptions> options) : ISmsSender
{
    private readonly TwilioOptions _options = options.Value.Twilio;

    public string Name => "Twilio";
    public string Description => "Twilio";

    public async Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccountSid) || string.IsNullOrWhiteSpace(_options.AuthToken) || string.IsNullOrWhiteSpace(_options.From))
        {
            return new SmsResult(SmsOutcome.Failed, Error: "Sms:Twilio:AccountSid, AuthToken and From must all be configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(_options.AccountSid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = phone,
                ["From"] = _options.From,
                ["Body"] = message,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}")));

        return await SmsHttp.SendAsync(async () =>
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var sid = JsonDocument.Parse(body).RootElement.TryGetProperty("sid", out var s) ? s.GetString() : null;
                return new SmsResult(SmsOutcome.Sent, sid);
            }
            if ((int)response.StatusCode >= 500)
            {
                return new SmsResult(SmsOutcome.Unknown, Error: $"Twilio HTTP {(int)response.StatusCode}");
            }
            string? error = null;
            try
            {
                var root = JsonDocument.Parse(body).RootElement;
                error = $"{root.GetProperty("message").GetString()} (code {root.GetProperty("code")})";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
            return new SmsResult(SmsOutcome.Failed, Error: error ?? $"Twilio HTTP {(int)response.StatusCode}: {body}");
        });
    }
}

internal static class SmsHttp
{
    /// <summary>Classifies transport errors: failures before the request left the machine are retryable, the rest are unknown.</summary>
    public static async Task<SmsResult> SendAsync(Func<Task<SmsResult>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError)
        {
            return new SmsResult(SmsOutcome.Failed, Error: ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new SmsResult(SmsOutcome.Unknown, Error: ex.Message);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return new SmsResult(SmsOutcome.Unknown, Error: "Timed out waiting for the SMS provider.");
        }
        catch (JsonException ex)
        {
            return new SmsResult(SmsOutcome.Unknown, Error: "Unexpected provider response: " + ex.Message);
        }
    }
}
