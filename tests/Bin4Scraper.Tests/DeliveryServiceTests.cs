using Bin4Scraper.Data;
using Bin4Scraper.Services;
using Bin4Scraper.Sms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Tests;

public sealed class DeliveryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bin4scraper-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Store _store;

    public DeliveryServiceTests()
    {
        _store = new Store(Options.Create(new StorageOptions { DataDirectory = _dir }));
        _store.Initialize();
    }

    [Fact]
    public async Task Concurrent_dispatchers_only_send_a_delivery_once()
    {
        var sender = new BlockingSender();
        var first = CreateService(sender);
        var second = CreateService(sender);
        var delivery = new Delivery { Phone = "+12505550100", Message = "hello", CreatedAt = DateTimeOffset.UtcNow };
        _store.AddDelivery(delivery);

        var firstDispatch = first.DispatchPendingAsync(default);
        await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.DispatchPendingAsync(default);
        Assert.Equal(DeliveryStatus.Unknown, _store.GetDelivery(delivery.Id)!.Status);
        Assert.Equal(1, sender.Calls);

        sender.Release.SetResult(new SmsResult(SmsOutcome.Sent, "test-id"));
        await firstDispatch;

        var recorded = _store.GetDelivery(delivery.Id)!;
        Assert.Equal(DeliveryStatus.Sent, recorded.Status);
        Assert.Equal(1, recorded.Attempts);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task Immediate_sends_are_claimed_before_calling_provider()
    {
        var sender = new InspectingSender(_store);
        var service = CreateService(sender);

        var delivery = await service.SendNowAsync("+12505550100", "hello", DeliveryKind.Test, null, default);

        Assert.Equal(DeliveryStatus.Unknown, sender.StatusWhenCalled);
        Assert.Equal(DeliveryStatus.Sent, delivery.Status);
        Assert.Equal(1, delivery.Attempts);
    }

    private DeliveryService CreateService(ISmsSender sender) =>
        new(_store, sender, Options.Create(new SmsOptions { DelayBetweenMessagesMs = 0 }), TimeProvider.System,
            NullLogger<DeliveryService>.Instance);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class BlockingSender : ISmsSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SmsResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string Name => "Blocking";
        public string Description => "Blocking test sender";

        public async Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
        {
            Calls++;
            Started.SetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class InspectingSender(Store store) : ISmsSender
    {
        public string? StatusWhenCalled { get; private set; }
        public string Name => "Inspecting";
        public string Description => "Inspecting test sender";

        public Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
        {
            StatusWhenCalled = store.GetRecentDeliveries(1).Single().Status;
            return Task.FromResult(new SmsResult(SmsOutcome.Sent, "test-id"));
        }
    }
}
