using Bin4Scraper.Data;
using Bin4Scraper.Sms;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Services;

/// <summary>Sends queued deliveries one at a time and records each provider response.</summary>
public sealed class DeliveryService(
    Store store,
    ISmsSender sender,
    IOptions<SmsOptions> options,
    TimeProvider time,
    ILogger<DeliveryService> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ISmsSender Sender => sender;

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var pending = store.GetPendingDeliveries();
            for (var i = 0; i < pending.Count; i++)
            {
                if (i > 0 && options.Value.DelayBetweenMessagesMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(options.Value.DelayBetweenMessagesMs), time, cancellationToken);
                }
                await AttemptAsync(pending[i], retryable: true, cancellationToken);
            }
            return pending.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sends a single message immediately (no automatic retry) and returns the recorded delivery.</summary>
    public async Task<Delivery> SendNowAsync(string phone, string message, string kind, long? recipientId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var delivery = new Delivery
            {
                Phone = phone,
                Message = message,
                Kind = kind,
                RecipientId = recipientId,
                Status = DeliveryStatus.Pending,
                CreatedAt = time.GetUtcNow(),
            };
            store.AddDelivery(delivery);
            await AttemptAsync(delivery, retryable: false, cancellationToken);
            return delivery;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Queues a copy of an earlier delivery and dispatches it.</summary>
    public async Task<Delivery?> ResendAsync(long deliveryId, CancellationToken cancellationToken)
    {
        var original = store.GetDelivery(deliveryId);
        if (original is null)
        {
            return null;
        }
        return await SendNowAsync(original.Phone, original.Message, original.Kind, original.RecipientId, cancellationToken);
    }

    private async Task AttemptAsync(Delivery delivery, bool retryable, CancellationToken cancellationToken)
    {
        if (!store.TryClaimDelivery(delivery, sender.Name, time.GetUtcNow()))
        {
            return;
        }

        SmsResult result;
        try
        {
            result = await sender.SendAsync(delivery.Phone, delivery.Message, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            result = new SmsResult(SmsOutcome.Unknown, Error: ex.Message);
        }

        delivery.ProviderMessageId = result.MessageId;
        delivery.Error = result.Error ?? result.Detail;
        delivery.Status = result.Outcome switch
        {
            SmsOutcome.Sent => DeliveryStatus.Sent,
            SmsOutcome.Unknown => DeliveryStatus.Unknown,
            _ when retryable && delivery.Attempts < options.Value.MaxAttempts => DeliveryStatus.Pending,
            _ => DeliveryStatus.Failed,
        };
        store.UpdateDelivery(delivery);

        if (result.Outcome == SmsOutcome.Sent)
        {
            logger.LogInformation("Delivery {Id} ({Kind}) sent to {Phone} via {Provider} ({MessageId})",
                delivery.Id, delivery.Kind, PhoneNumber.Mask(delivery.Phone), sender.Name, result.MessageId);
        }
        else
        {
            logger.LogWarning("Delivery {Id} ({Kind}) to {Phone} via {Provider} is {Status} after attempt {Attempt}: {Error}",
                delivery.Id, delivery.Kind, PhoneNumber.Mask(delivery.Phone), sender.Name, delivery.Status, delivery.Attempts, result.Error);
        }
    }
}
