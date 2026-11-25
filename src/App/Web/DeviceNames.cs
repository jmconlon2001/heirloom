namespace FamilyMemories.Web;

/// <summary>Turns a browser's User-Agent into something an admin recognises ("iPad (Safari)").</summary>
public static class DeviceNames
{
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Unknown device";
        var ua = userAgent;
        var device = ua.Contains("iPad") ? "iPad"
            : ua.Contains("iPhone") ? "iPhone"
            : ua.Contains("Android") ? (ua.Contains("Mobile") ? "Android phone" : "Android tablet")
            : ua.Contains("Macintosh") ? (ua.Contains("Mobile/") ? "iPad" : "Mac")
            : ua.Contains("Windows") ? "Windows computer"
            : ua.Contains("CrOS") ? "Chromebook"
            : ua.Contains("Linux") ? "Linux computer"
            : "Device";
        var browser = ua.Contains("Edg/") ? "Edge"
            : ua.Contains("CriOS") || (ua.Contains("Chrome/") && !ua.Contains("Edg/")) ? "Chrome"
            : ua.Contains("FxiOS") || ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Safari/") ? "Safari"
            : null;
        return browser is null ? device : $"{device} ({browser})";
    }
}
