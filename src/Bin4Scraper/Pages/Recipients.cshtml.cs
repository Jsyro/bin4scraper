using Bin4Scraper.Data;
using Bin4Scraper.Sms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Pages;

public sealed class RecipientsModel(Store store, IOptions<SmsOptions> smsOptions) : AppPageModel
{
    public IReadOnlyList<Recipient> Recipients { get; private set; } = [];

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Phone { get; set; }

    public void OnGet() => Recipients = store.GetRecipients();

    public IActionResult OnPostAdd()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ModelState.AddModelError(nameof(Name), "Name is required.");
        }
        if (!PhoneNumber.TryNormalize(Phone, smsOptions.Value.DefaultCountryCode, out var phone))
        {
            ModelState.AddModelError(nameof(Phone), "Enter a valid phone number, e.g. 250-555-1234 or +12505551234.");
        }
        if (!ModelState.IsValid)
        {
            OnGet();
            return Page();
        }

        try
        {
            store.AddRecipient(Name!.Trim(), phone!);
            Flash($"Added {Name.Trim()} ({phone}).");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            Flash($"{phone} is already a recipient.", ok: false);
        }
        return RedirectToPage();
    }

    public IActionResult OnPostToggle(long id)
    {
        if (store.GetRecipient(id) is { } recipient)
        {
            store.SetRecipientEnabled(id, !recipient.Enabled);
            Flash($"{recipient.Name} {(recipient.Enabled ? "disabled" : "enabled")}.");
        }
        return RedirectToPage();
    }

    public IActionResult OnPostDelete(long id)
    {
        if (store.GetRecipient(id) is { } recipient)
        {
            store.DeleteRecipient(id);
            Flash($"Removed {recipient.Name}.");
        }
        return RedirectToPage();
    }
}
