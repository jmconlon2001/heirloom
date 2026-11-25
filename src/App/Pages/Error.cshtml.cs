using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages;

/// <summary>Friendly error page. Never shows codes or stack traces.</summary>
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel : PageModel
{
    public string Heading { get; private set; } = "";
    public string Message { get; private set; } = "";

    public void OnGet(int? code) => Describe(code);
    public void OnPost(int? code) => Describe(code);

    private void Describe(int? code)
    {
        (Heading, Message) = code switch
        {
            404 => ("We couldn't find that page",
                "The link may be old or mistyped. Try going back to the home page."),
            403 => ("This page isn't available to you",
                "If you think you should be able to see it, ask the family admin."),
            429 => ("Let's take a short break",
                "There have been a lot of tries in a short time. Please wait 10 minutes, then try again."),
            400 => ("That didn't go through",
                "Please go back and try again. If you were writing, your words are still saved on this device."),
            _ => ("Something went wrong on our side",
                "It's not your fault. Please try again in a minute. Anything you saved is safe."),
        };
        if (code is >= 400 and < 600) Response.StatusCode = code.Value;
        else Response.StatusCode = StatusCodes.Status500InternalServerError;
    }
}
