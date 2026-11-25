using System.Security.Claims;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Auth;

/// <summary>
/// "Remembered devices": signing in with a magic link creates a UserDevice row and a
/// persistent cookie that carries its id. Each request checks the device is still valid,
/// so an admin (or "Sign out") can revoke one browser without touching the others.
/// </summary>
public sealed class DeviceService(
    AppDbContext db,
    SignInManager<AppUser> signIn,
    AppOptions options,
    TimeProvider clock)
{
    public const string DeviceClaim = "fm:device";

    public async Task SignInNewDeviceAsync(AppUser user, HttpContext http)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var device = new UserDevice
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 500),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.Add(options.RememberedDeviceLifetime),
        };
        db.UserDevices.Add(device);

        await db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, now));
        await db.SaveChangesAsync();

        await signIn.SignInWithClaimsAsync(user,
            new AuthenticationProperties { IsPersistent = true, AllowRefresh = true },
            [new Claim(DeviceClaim, device.Id.ToString())]);
    }

    public async Task SignOutAsync(HttpContext http)
    {
        if (TryGetDeviceId(http.User, out var id))
        {
            await db.UserDevices.Where(d => d.Id == id && d.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, clock.GetUtcNow().UtcDateTime));
        }
        await signIn.SignOutAsync();
    }

    /// <summary>Cookie OnValidatePrincipal hook.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null || !TryGetDeviceId(context.Principal, out var deviceId))
        {
            await Reject(context);
            return;
        }

        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var options = services.GetRequiredService<AppOptions>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

        var device = await db.UserDevices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => new { d.UserId, d.RevokedAt, d.ExpiresAt, d.LastSeenAt, d.User!.LockoutEnd })
            .SingleOrDefaultAsync();

        var userId = context.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (device is null || device.RevokedAt is not null || device.ExpiresAt <= now
            || device.UserId.ToString() != userId
            || (device.LockoutEnd is { } end && end.UtcDateTime > now))
        {
            await Reject(context);
            return;
        }

        // Sliding window: touch the device at most once an hour to keep writes rare.
        if (now - device.LastSeenAt > TimeSpan.FromHours(1))
        {
            await db.UserDevices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.LastSeenAt, now)
                .SetProperty(d => d.ExpiresAt, now.Add(options.RememberedDeviceLifetime)));
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }

    private static bool TryGetDeviceId(ClaimsPrincipal principal, out Guid id) =>
        Guid.TryParse(principal.FindFirstValue(DeviceClaim), out id);

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
}
