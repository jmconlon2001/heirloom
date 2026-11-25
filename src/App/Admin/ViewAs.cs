using System.Security.Claims;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace FamilyMemories.Admin;

/// <summary>
/// Admin "view as": shows exactly what another person sees, by swapping in their identity for each
/// request, so every access rule applies unchanged. Strictly read-only: any change is refused,
/// except "Stop". Starting it is audit-logged.
/// </summary>
public static class ViewAs
{
    public const string CookieName = "fm_viewas";
    public const string StopPath = "/view-as/stop";
    public const string AdminNameKey = "fm:viewas-admin";
    public const string TargetNameKey = "fm:viewas-target";

    private static IDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("view-as");

    public static void Start(HttpContext http, Guid adminId, Guid targetId) =>
        http.Response.Cookies.Append(CookieName, Protector(http).Protect($"{adminId}|{targetId}"), new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, IsEssential = true,
        });

    public static bool IsActive(HttpContext http) => http.Items.ContainsKey(AdminNameKey);

    public static IApplicationBuilder UseViewAs(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var cookie = http.Request.Cookies[CookieName];
        if (cookie is null || http.User.Identity?.IsAuthenticated != true || !http.User.IsAdmin())
        {
            await next();
            return;
        }

        Guid adminId, targetId;
        try
        {
            var parts = Protector(http).Unprotect(cookie).Split('|');
            (adminId, targetId) = (Guid.Parse(parts[0]), Guid.Parse(parts[1]));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or IndexOutOfRangeException)
        {
            http.Response.Cookies.Delete(CookieName);
            await next();
            return;
        }
        if (adminId != http.User.UserId())
        {
            http.Response.Cookies.Delete(CookieName);
            await next();
            return;
        }

        var users = http.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var target = await users.FindByIdAsync(targetId.ToString());
        if (target is null)
        {
            http.Response.Cookies.Delete(CookieName);
            await next();
            return;
        }

        var adminName = http.User.DisplayName();
        var factory = http.RequestServices.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>();
        http.User = await factory.CreateAsync(target);
        http.Items[AdminNameKey] = adminName;
        http.Items[TargetNameKey] = target.DisplayName;

        // Stopping only gives the admin their own view back, so it needs no CSRF check.
        if (HttpMethods.IsPost(http.Request.Method) && http.Request.Path.Equals(StopPath, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.Cookies.Delete(CookieName);
            http.Response.Redirect($"/admin/users/{targetId}");
            return;
        }
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync($"You're viewing the site as {target.DisplayName}, which is read-only. Stop viewing as {target.DisplayName} to make changes.");
            return;
        }
        await next();
    });
}
