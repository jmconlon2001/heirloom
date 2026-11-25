using System.Security.Claims;
using FamilyMemories.Data;

namespace FamilyMemories.Auth;

public static class CurrentUserExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("Not signed in");

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);
}
