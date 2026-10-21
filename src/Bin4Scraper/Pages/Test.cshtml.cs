using Bin4Scraper.Data;
using Bin4Scraper.Services;
using Bin4Scraper.Sms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Pages;

public sealed class TestModel(Store store, DeliveryService deliveries, CheckService checks, IOptions<SmsOptions> smsOptions) : AppPageModel
{
    public IReadOnlyList<Recipient> Recipients { get; private set; } = [];
    public Delivery? Result { get; private set; }
    public string ProviderDescription => deliveries.Sender.Description;

    [BindProperty(SupportsGet = true)] public string? Phone { get; set; }
    [BindProperty] public string? Message { get; set; }

    public void OnGet()
    {
        Recipients = store.GetRecipients();
        Message ??= $"Bin4Scraper test message from {Environment.MachineName} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}.";
    }

    public async Task<IActionResult> OnPostSendAsync()
    {
        if (!PhoneNumber.TryNormalize(Phone, smsOptions.Value.DefaultCountryCode, out var phone))
        {
            ModelState.AddModelError(nameof(Phone), "Enter a valid phone number.");
        }
        if (string.IsNullOrWhiteSpace(Message))
        {
            ModelState.AddModelError(nameof(Message), "Message is required.");
        }
        if (ModelState.IsValid)
        {
            var recipientId = store.GetRecipients().FirstOrDefault(r => r.Phone == phone)?.Id;
            Result = await deliveries.SendNowAsync(phone!, Message!.Trim(), DeliveryKind.Test, recipientId, CancellationToken.None);
        }
        OnGet();
        return Page();
    }

    public async Task<IActionResult> OnPostUseFeatureAsync()
    {
        try
        {
            Message = (await checks.PreviewAsync(HttpContext.RequestAborted)).Message;
            ModelState.Remove(nameof(Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ModelState.AddModelError(nameof(Message), "Could not load the live feature: " + ex.Message);
        }
        OnGet();
        return Page();
    }
}
