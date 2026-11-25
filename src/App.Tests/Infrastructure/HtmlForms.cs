using System.Net;
using System.Text.RegularExpressions;

namespace FamilyMemories.Tests.Infrastructure;

public static partial class HtmlForms
{
    /// <summary>Reads the antiforgery token from the first form on the page that has one.</summary>
    public static string AntiforgeryToken(string html)
    {
        var match = TokenRegex().Match(html);
        if (!match.Success) throw new InvalidOperationException("No antiforgery token on page");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, string pageHtml,
        params (string Key, string Value)[] fields)
    {
        var values = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).ToList();
        values.Add(new("__RequestVerificationToken", AntiforgeryToken(pageHtml)));
        return await client.PostAsync(url, new FormUrlEncodedContent(values));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
