using Bin4Scraper.Data;
using Bin4Scraper.Services;
using Microsoft.AspNetCore.Mvc;

namespace Bin4Scraper.Pages;

public sealed class HistoryModel(Store store, DeliveryService deliveries) : AppPageModel
{
    public IReadOnlyList<CheckRun> Checks { get; private set; } = [];
    public IReadOnlyList<Delivery> Deliveries { get; private set; } = [];

    public void OnGet()
    {
        Checks = store.GetRecentChecks(50);
        Deliveries = store.GetRecentDeliveries(100);
    }

    public async Task<IActionResult> OnPostResendAsync(long id)
    {
        var delivery = await deliveries.ResendAsync(id, CancellationToken.None);
        if (delivery is null)
        {
            Flash("Delivery not found.", ok: false);
        }
        else
        {
            Flash($"Resent to {delivery.Phone}: {delivery.Status}{(delivery.Error is null ? "" : " — " + delivery.Error)}",
                ok: delivery.Status == DeliveryStatus.Sent);
        }
        return RedirectToPage();
    }
}
