using System.Net.Http.Headers;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Admin;

/// <summary>Tells every admin when something needs attention (failed backup, failed verify): email, plus ntfy if configured.</summary>
public sealed class AdminAlerts(AppDbContext db, IEmailSender email, IHttpClientFactory http, AppOptions options, ILogger<AdminAlerts> log)
{
    public async Task SendAsync(string subject, string message, CancellationToken ct = default)
    {
        log.LogWarning("Admin alert: {Subject}: {Message}", subject, message);
        var admins = await (from u in db.Users
                            join ur in db.UserRoles on u.Id equals ur.UserId
                            join r in db.Roles on ur.RoleId equals r.Id
                            where r.Name == Roles.Admin && u.Email != null
                            select new { u.Email, u.DisplayName }).ToListAsync(ct);

        foreach (var admin in admins)
        {
            try
            {
                var text = $"{message}\n\nOpen {options.BuildUrl("/admin/backups")} to see details.";
                var html = $"<p style=\"font-family:Arial,sans-serif;font-size:18px\">{System.Net.WebUtility.HtmlEncode(message)}</p>" +
                           $"<p><a href=\"{options.BuildUrl("/admin/backups")}\">See the details</a></p>";
                await email.SendAsync(new EmailMessage(admin.Email!, admin.DisplayName, $"[{options.AppName}] {subject}", text, html), ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not email admin alert");
            }
        }

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
