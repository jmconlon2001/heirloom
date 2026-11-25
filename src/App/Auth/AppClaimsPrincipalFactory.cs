using System.Security.Claims;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FamilyMemories.Auth;

public sealed class AppClaimsPrincipalFactory(
    UserManager<AppUser> users, RoleManager<AppRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(users, roles, options)
{
    public const string DisplayNameClaim = "fm:display_name";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(DisplayNameClaim, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? "" : user.DisplayName));
        return identity;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(AppClaimsPrincipalFactory.DisplayNameClaim) ?? user.Identity?.Name ?? "";
}
