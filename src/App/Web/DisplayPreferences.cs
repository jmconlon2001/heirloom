namespace FamilyMemories.Web;

/// <summary>
/// Text size (A / A+ / A++) and high-contrast choice. Stored in a long-lived cookie so it
/// applies before sign-in and on the very first paint (the server writes the classes on &lt;html&gt;).
/// Cookie value format: "s{0-2}.c{0|1}", e.g. "s2.c1".
/// </summary>
public sealed record DisplayPreferences(int TextSize, bool HighContrast)
{
    public const string CookieName = "fm_display";
    public static readonly DisplayPreferences Default = new(0, false);
    public static readonly string[] SizeLabels = ["A", "A+", "A++"];
    public static readonly string[] SizeNames = ["Normal text", "Larger text", "Largest text"];

    public string HtmlClasses => $"text-{TextSize}{(HighContrast ? " high-contrast" : "")}";

    public string ToCookieValue() => $"s{TextSize}.c{(HighContrast ? 1 : 0)}";

    public static DisplayPreferences Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Default;
        int size = 0;
        bool contrast = false;
        foreach (var part in value.Split('.'))
        {
            if (part.Length == 2 && part[0] == 's' && part[1] is >= '0' and <= '2') size = part[1] - '0';
            else if (part is "c1") contrast = true;
        }
        return new DisplayPreferences(size, contrast);
    }

    public static DisplayPreferences From(HttpContext http) =>
        http.Items.TryGetValue(CookieName, out var cached) && cached is DisplayPreferences p
            ? p
            : Parse(http.Request.Cookies[CookieName]);

    public void Save(HttpContext http)
    {
        http.Items[CookieName] = this;
        http.Response.Cookies.Append(CookieName, ToCookieValue(), new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(5),
            HttpOnly = false, // the enhancement script updates it without a page reload
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
    }
}
