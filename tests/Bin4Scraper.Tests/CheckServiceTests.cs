using Bin4Scraper.Data;
using Bin4Scraper.Scraping;
using Bin4Scraper.Services;
using Bin4Scraper.Sms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Tests;

public sealed class CheckServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bin4scraper-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource _source = new();
    private readonly FakeSender _sender = new();
    private readonly Store _store;
    private readonly CheckService _checks;

    public CheckServiceTests()
    {
        _store = new Store(Options.Create(new StorageOptions { DataDirectory = _dir }));
        _store.Initialize();
        var sms = Options.Create(new SmsOptions { DelayBetweenMessagesMs = 0, AdminPhone = "+12505550199", FailureAlertThreshold = 2 });
        var deliveries = new DeliveryService(_store, _sender, sms, TimeProvider.System, NullLogger<DeliveryService>.Instance);
        _checks = new CheckService(_source, _store, deliveries, Options.Create(new ScraperOptions()), sms,
            TimeProvider.System, NullLogger<CheckService>.Instance);
        _store.AddRecipient("Alice", "+12505550100");
        _store.AddRecipient("Bob", "+12505550101");
    }

    [Fact]
    public async Task Notifies_on_first_run_and_on_change_only()
    {
        _source.Html = Page("Bin 1 - First");
        var first = await _checks.RunAsync("test", default);
        Assert.Equal(CheckStatus.Succeeded, first.Status);
        Assert.True(first.Changed);
        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("Bin 1 - First", _sender.Sent[0].Message);

        var unchanged = await _checks.RunAsync("test", default);
        Assert.False(unchanged.Changed);
        Assert.Equal(2, _sender.Sent.Count);

        _source.Html = Page("Bin 2 - Second");
        var changed = await _checks.RunAsync("test", default);
        Assert.True(changed.Changed);
        Assert.Equal(4, _sender.Sent.Count);
        Assert.All(_store.GetRecentDeliveries(10), d => Assert.Equal(DeliveryStatus.Sent, d.Status));
    }

    [Fact]
    public async Task Disabled_recipients_are_skipped()
    {
        _store.SetRecipientEnabled(_store.GetRecipients().First(r => r.Name == "Bob").Id, false);
        _source.Html = Page("Bin 1 - First");
        await _checks.RunAsync("test", default);
        Assert.Equal("+12505550100", Assert.Single(_sender.Sent).Phone);
    }

    [Fact]
    public async Task Alerts_admin_once_after_threshold_consecutive_failures()
    {
        _source.Html = "<html><body>maintenance</body></html>";
        for (var i = 0; i < 4; i++)
        {
            var run = await _checks.RunAsync("test", default);
            Assert.Equal(CheckStatus.Failed, run.Status);
        }

        var alert = Assert.Single(_sender.Sent);
        Assert.Equal("+12505550199", alert.Phone);
    }

    [Fact]
    public async Task Unknown_outcome_is_not_retried()
    {
        _sender.Outcome = SmsOutcome.Unknown;
        _source.Html = Page("Bin 1 - First");
        await _checks.RunAsync("test", default);
        Assert.Equal(2, _sender.Sent.Count);

        _sender.Outcome = SmsOutcome.Sent;
        // Dispatching again must not re-send messages whose delivery state is unknown.
        await _checks.RunAsync("test", default);
        Assert.Equal(2, _sender.Sent.Count);
        Assert.All(_store.GetRecentDeliveries(10), d => Assert.Equal(DeliveryStatus.Unknown, d.Status));
    }

    private static string Page(string burger) => $"""
        <html><body><section>
          <h2>November Features</h2>
          <h6>Burger</h6>
          <div class="xd-menu-item">
            <p class="xd-menu-item__heading"><span>{burger}</span><span>25</span></p>
            <p class="wp-block-paragraph">Beef.</p>
          </div>
        </section></body></html>
        """;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeSource : IFeatureSource
    {
        public string Html { get; set; } = "";
        public Uri Url { get; } = new("https://example.test/monthly-features/");
        public Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(Html);
    }

    private sealed class FakeSender : ISmsSender
    {
        public List<(string Phone, string Message)> Sent { get; } = [];
        public SmsOutcome Outcome { get; set; } = SmsOutcome.Sent;
        public string Name => "Fake";
        public string Description => "Fake";

        public Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
        {
            Sent.Add((phone, message));
            return Task.FromResult(Outcome == SmsOutcome.Sent
                ? new SmsResult(SmsOutcome.Sent, "id-" + Sent.Count)
                : new SmsResult(Outcome, Error: "simulated"));
        }
    }
}
