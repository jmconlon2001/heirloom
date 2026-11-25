using System.Net;
using FamilyMemories.Configuration;

namespace FamilyMemories.Email;

/// <summary>
/// Built-in email bodies. Phase 4 replaces these with admin-editable templates;
/// the wording rules stay the same: large type, one big button, plain words.
/// </summary>
public sealed class EmailTemplates(AppOptions options)
{
    public EmailMessage LoginLink(string toAddress, string toName, Uri link)
    {
        var app = options.AppName;
        var help = HelpLine();
        var text = $"""
            Hello {toName},

            Here is your link to sign in to {app}:

            {link}

            The link works once and stays good for 14 days. After you use it, this device will remember you.

            If you didn't ask for this, you can ignore this email.
            {help}
            """;

        var html = $"""
            <!doctype html>
            <html lang="en"><body style="margin:0;padding:24px;background:#fffdf8;font-family:Georgia,'Times New Roman',serif;color:#1f1b16;font-size:20px;line-height:1.6">
              <p style="font-size:22px;margin:0 0 16px">Hello {Enc(toName)},</p>
              <p style="margin:0 0 24px">Tap the big button to sign in to {Enc(app)}.</p>
              <p style="margin:0 0 24px">
                <a href="{Enc(link.ToString())}" style="display:inline-block;background:#1d4f91;color:#ffffff;text-decoration:none;font-family:Arial,sans-serif;font-size:24px;font-weight:bold;padding:18px 32px;border-radius:10px">Sign me in</a>
              </p>
              <p style="margin:0 0 16px">The button works once and stays good for 14 days. After you use it, this device will remember you.</p>
              <p style="margin:0 0 16px;color:#4a4238">If you didn't ask for this, you can ignore this email.</p>
              {(help.Length > 0 ? $"<p style=\"margin:24px 0 0;color:#4a4238\">{Enc(help.Trim())}</p>" : "")}
            </body></html>
            """;

        return new EmailMessage(toAddress, toName, $"Your sign-in link for {app}", text, html);
    }

    private string HelpLine() =>
        string.IsNullOrWhiteSpace(options.HelpName) && string.IsNullOrWhiteSpace(options.HelpPhone)
            ? ""
            : $"\nNeed help? Call {options.HelpName} {options.HelpPhone}".TrimEnd();

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
