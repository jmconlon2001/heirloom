using System.Net.Http.Headers;
using System.Text.Json;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Admin;

/// <summary>The newest release seen on GitHub, when it's newer than this server's version.</summary>
public sealed record AvailableUpdate(string Version, string Url, DateTime CheckedAt)
{
    public const string SettingKey = "update.latest";

    public static async Task<AvailableUpdate?> LoadAsync(AppDbContext db, string currentVersion)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == SettingKey);
        var latest = row is null ? null : JsonSerializer.Deserialize<AvailableUpdate>(row.Value);
        return latest is not null && UpdateChecker.IsNewer(latest.Version, currentVersion) ? latest : null;
    }
}

/// <summary>
/// Once a day, asks GitHub for the latest release (UPDATE_CHECK=false switches this off). The request carries no
/// information about the family: just a generic User-Agent, as GitHub requires.
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory http, AppDbContext db, AppOptions options, TimeProvider clock, ILogger<UpdateChecker> log)
{
    public const string ClientName = "updates";

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!options.UpdateCheck) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{options.UpdateRepository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Heirloom-update-check", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.CreateClient(ClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) { log.LogInformation("Update check: GitHub answered {Status}", (int)response.StatusCode); return; }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            await SaveAsync(new AvailableUpdate(tag.TrimStart('v'), url, clock.GetUtcNow().UtcDateTime));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            log.LogInformation(ex, "Update check failed; will try again tomorrow");
        }
    }

    private async Task SaveAsync(AvailableUpdate latest)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == AvailableUpdate.SettingKey);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = AvailableUpdate.SettingKey });
        row.Value = JsonSerializer.Serialize(latest);
        row.UpdatedAt = latest.CheckedAt;
        await db.SaveChangesAsync();
    }

    /// <summary>"0.12.0" is newer than "0.11.0"; a development build ("dev", "0.0.0-dev") never asks to be updated.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(candidate.Split('-')[0], out var c)) return false;
        if (!Version.TryParse(current.Split('-')[0], out var mine) || current.Contains("dev") || mine == new Version(0, 0, 0)) return false;
        return c > mine;
    }
}
