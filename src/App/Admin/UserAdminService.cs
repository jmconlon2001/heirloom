using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Admin;

public sealed record UserInput(
    string DisplayName,
    string Email,
    IReadOnlyCollection<string> Roles,
    Guid? PromptSetId,
    Pacing Pacing);

public sealed record AdminResult(bool Succeeded, string? Error = null, Guid? UserId = null)
{
    public static AdminResult Ok(Guid? id = null) => new(true, UserId: id);
    public static AdminResult Fail(string error) => new(false, error);
}

/// <summary>Everything an admin does to people. Every change is audit-logged.</summary>
public sealed class UserAdminService(
    AppDbContext db,
    UserManager<AppUser> users,
    MagicLinkService links,
    EmailService email,
    AuditLog audit,
    TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<AdminResult> InviteAsync(UserInput input, bool sendEmail, string inviterName)
    {
        var address = input.Email.Trim();
        if (await users.FindByEmailAsync(address) is not null)
            return AdminResult.Fail("Someone with that email address is already on the list.");

        var user = new AppUser
        {
            UserName = address,
            Email = address,
            EmailConfirmed = true,
            DisplayName = input.DisplayName.Trim(),
            CreatedAt = Now,
            PromptSetId = input.PromptSetId,
            Pacing = input.Pacing,
            PacingStartedAt = input.PromptSetId is null ? null : Now,
        };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded) return AdminResult.Fail(Describe(created));

        var roles = NormalizeRoles(input.Roles, input.PromptSetId);
        await users.AddToRolesAsync(user, roles);
        audit.Add("user.invite", $"Added {user.DisplayName} <{address}> as {string.Join(", ", roles)}", "user", user.Id);
        await db.SaveChangesAsync();

        if (sendEmail) await SendInvitationAsync(user, inviterName);
        return AdminResult.Ok(user.Id);
    }

    public async Task SendInvitationAsync(AppUser user, string inviterName)
    {
        var token = await links.IssueAsync(user, MagicLinkPurpose.Invitation, "/", null);
        if (token is null) return;
        user.InvitedAt = Now;
        audit.Add("user.invite-email", $"Sent an invitation email to {user.DisplayName}", "user", user.Id);
        await db.SaveChangesAsync();
        await email.SendAsync(EmailKind.Invitation, new Recipient(user.Email!, user.DisplayName, user.Id),
            new Dictionary<string, object?> { ["inviter_name"] = inviterName, ["link"] = links.BuildUrl(token).ToString() });
    }

    public async Task<AdminResult> UpdateAsync(Guid userId, UserInput input, Guid actingAdminId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return AdminResult.Fail("That person couldn't be found.");

        var address = input.Email.Trim();
        if (!string.Equals(user.Email, address, StringComparison.OrdinalIgnoreCase))
        {
            if (await users.FindByEmailAsync(address) is not null)
                return AdminResult.Fail("Someone else already uses that email address.");
            await users.SetEmailAsync(user, address);
            await users.SetUserNameAsync(user, address);
            user.EmailConfirmed = true;
        }

        if (user.PromptSetId is null && input.PromptSetId is not null) user.PacingStartedAt = Now;
        user.DisplayName = input.DisplayName.Trim();
        user.PromptSetId = input.PromptSetId;
        user.Pacing = input.Pacing;

        var wanted = NormalizeRoles(input.Roles, input.PromptSetId);
        var current = await users.GetRolesAsync(user);
        if (userId == actingAdminId && current.Contains(Roles.Admin) && !wanted.Contains(Roles.Admin))
            return AdminResult.Fail("You can't remove your own admin role. Ask another admin to do it.");

        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) return AdminResult.Fail(Describe(updated));
        await users.RemoveFromRolesAsync(user, current.Except(wanted));
        await users.AddToRolesAsync(user, wanted.Except(current));

        audit.Add("user.update", $"Updated {user.DisplayName}: roles {string.Join(", ", wanted)}, pacing {input.Pacing}", "user", user.Id);
        await db.SaveChangesAsync();
        return AdminResult.Ok(user.Id);
    }

    /// <summary>A storyteller must have the Storyteller role, and everyone can at least read.</summary>
    private static List<string> NormalizeRoles(IEnumerable<string> roles, Guid? promptSetId)
    {
        var set = roles.Where(r => Roles.All.Contains(r)).ToHashSet();
        if (promptSetId is not null) set.Add(Roles.Storyteller);
        set.Add(Roles.Family);
        return [.. Roles.All.Where(set.Contains)];
    }

    /// <summary>A single-use sign-in link for a printed QR login card.</summary>
    public async Task<string?> CreateLoginCardAsync(AppUser user)
    {
        var token = await links.IssueAsync(user, MagicLinkPurpose.DeviceEnrollment, "/", null);
        audit.Add("logincard.print", $"Printed a login card for {user.DisplayName}", "user", user.Id);
        await db.SaveChangesAsync();
        return token;
    }

    public async Task<bool> RevokeDeviceAsync(Guid userId, Guid deviceId)
    {
        var device = await db.UserDevices.Include(d => d.User)
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId && d.RevokedAt == null);
        if (device is null) return false;
        device.RevokedAt = Now;
        audit.Add("device.revoke", $"Signed out a device for {device.User!.DisplayName} ({device.UserAgent})", "user", userId);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<AdminResult> SetPausedAsync(Guid userId, bool paused, Guid actingAdminId)
    {
        if (userId == actingAdminId && paused) return AdminResult.Fail("You can't pause your own account.");
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return AdminResult.Fail("That person couldn't be found.");
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, paused ? DateTimeOffset.MaxValue : null);
        audit.Add(paused ? "user.pause" : "user.unpause", $"{(paused ? "Paused" : "Unpaused")} {user.DisplayName}", "user", user.Id);
        await db.SaveChangesAsync();
        return AdminResult.Ok(user.Id);
    }

    public async Task<AdminResult> AddHelperAsync(Guid storytellerId, Guid helperId)
    {
        if (storytellerId == helperId) return AdminResult.Fail("Choose someone other than the storyteller.");
        if (await db.StorytellerHelpers.AnyAsync(h => h.StorytellerId == storytellerId && h.HelperId == helperId))
            return AdminResult.Ok();
        var names = await db.Users.Where(u => u.Id == storytellerId || u.Id == helperId).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        if (names.Count != 2) return AdminResult.Fail("That person couldn't be found.");

        db.StorytellerHelpers.Add(new StorytellerHelper { StorytellerId = storytellerId, HelperId = helperId, CreatedAt = Now });
        audit.Add("helper.add", $"{names[helperId]} can now write stories for {names[storytellerId]}", "user", storytellerId);
        await db.SaveChangesAsync();
        return AdminResult.Ok();
    }

    public async Task RemoveHelperAsync(Guid storytellerId, Guid helperId)
    {
        var row = await db.StorytellerHelpers.SingleOrDefaultAsync(h => h.StorytellerId == storytellerId && h.HelperId == helperId);
        if (row is null) return;
        db.StorytellerHelpers.Remove(row);
        audit.Add("helper.remove", $"Removed helper {helperId} from storyteller {storytellerId}", "user", storytellerId);
        await db.SaveChangesAsync();
    }

    /// <summary>A question just for this person; it goes to the front of their list.</summary>
    public async Task<Prompt> AddOneOffPromptAsync(Guid storytellerId, string question, string? title, Guid askedById)
    {
        question = question.Trim();
        var prompt = new Prompt
        {
            Id = Guid.CreateVersion7(),
            ForUserId = storytellerId,
            AskedByUserId = askedById,
            Title = string.IsNullOrWhiteSpace(title) ? Shorten(question, 80) : title.Trim(),
            Question = question,
            IsActive = true,
            CreatedAt = Now,
        };
        db.Prompts.Add(prompt);
        audit.Add("prompt.one-off", $"Added a question for storyteller {storytellerId}: {Shorten(question, 120)}", "prompt", prompt.Id);
        await db.SaveChangesAsync();
        return prompt;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private static string Describe(IdentityResult r) =>
        r.Errors.Any(e => e.Code is "InvalidEmail" or "InvalidUserName")
            ? "That email address doesn't look right. Please check it."
            : string.Join(" ", r.Errors.Select(e => e.Description));
}
