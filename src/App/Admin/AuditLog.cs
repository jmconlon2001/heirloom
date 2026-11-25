using System.Security.Claims;
using FamilyMemories.Auth;
using FamilyMemories.Data;

namespace FamilyMemories.Admin;

/// <summary>Append-only record of sensitive actions (admin changes, proxy writing, restores, login cards).</summary>
public sealed class AuditLog(AppDbContext db, IHttpContextAccessor http, TimeProvider clock)
{
    /// <summary>Adds an entry to the current unit of work; it is written with the caller's SaveChanges.</summary>
    public void Add(string action, string summary, string? targetType = null, object? targetId = null)
    {
        var context = http.HttpContext;
        var user = context?.User;
        Guid? actorId = user?.Identity?.IsAuthenticated == true ? user.UserId() : null;
        db.AuditLog.Add(new AuditEntry
        {
            At = clock.GetUtcNow().UtcDateTime,
            ActorId = actorId,
            ActorName = user?.Identity?.IsAuthenticated == true ? user.DisplayName() : "system",
            Action = action,
            TargetType = targetType,
            TargetId = targetId?.ToString(),
            Summary = summary.Length > 1000 ? summary[..1000] : summary,
            Ip = context?.Connection.RemoteIpAddress?.ToString(),
        });
    }

    public async Task WriteAsync(string action, string summary, string? targetType = null, object? targetId = null)
    {
        Add(action, summary, targetType, targetId);
        await db.SaveChangesAsync();
    }
}
