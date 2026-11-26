using System.Net.Http.Headers;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Admin;

/// <summary>Tells every admin when something needs attention (failed backup, failed verify): email, plus ntfy if configured.</summary>
public sealed class AdminAlerts(AppDbContext db, EmailService email, IHttpClientFactory http, AppOptions options, ILogger<AdminAlerts> log)
{
    public async Task SendAsync(string subject, string message, CancellationToken ct = default, string? linkPath = null)
    {
        log.LogWarning("Admin alert: {Subject}: {Message}", subject, message);
        foreach (var admin in await AdminsAsync(ct))
        {
            try
            {
                await email.SendAsync(EmailKind.AdminAlert, admin, new Dictionary<string, object?>
                {
                    ["subject"] = subject, ["message"] = message, ["link"] = options.BuildUrl(linkPath ?? "admin/backups").ToString(),
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not email admin alert");
            }
        }

        await NtfyAsync(subject, message, ct);
    }

    public async Task<List<Recipient>> AdminsAsync(CancellationToken ct = default) =>
        await (from u in db.Users
               join ur in db.UserRoles on u.Id equals ur.UserId
               join r in db.Roles on ur.RoleId equals r.Id
               where r.Name == Roles.Admin && u.Email != null && (u.LockoutEnd == null || u.LockoutEnd < DateTimeOffset.UtcNow)
               select new Recipient(u.Email!, u.DisplayName, u.Id)).ToListAsync(ct);

    public async Task NtfyAsync(string subject, string message, CancellationToken ct = default)
    {
        if (options.NtfyUrl is not null && options.NtfyTopic is not null)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{options.NtfyUrl.TrimEnd('/')}/{options.NtfyTopic}")
                {
                    Content = new StringContent(message),
                };
                request.Headers.Add("Title", $"{options.AppName}: {subject}");
                request.Headers.Add("Priority", "high");
                if (options.NtfyToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.NtfyToken);
                using var response = await http.CreateClient("ntfy").SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not send ntfy alert");
            }
        }
    }
}
