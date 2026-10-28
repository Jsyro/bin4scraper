using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bin4Scraper.Pages;

public abstract class AppPageModel : PageModel
{
    protected void Flash(string message, bool ok = true)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = ok ? "ok" : "error";
    }

    public static string StatusClass(string? status) => status switch
    {
        "succeeded" or "sent" => "ok",
        "failed" => "error",
        "unknown" or "pending" or "running" => "warn",
        _ => "",
    };
}
