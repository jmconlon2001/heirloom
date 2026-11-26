using Microsoft.AspNetCore.Identity;

namespace FamilyMemories.Data;

public sealed class AppUser : IdentityUser<Guid>
{
    /// <summary>How the app addresses this person ("Grandma Rose").</summary>
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // ---- Storyteller settings ----
    /// <summary>The prompt set this person is answering. Null = not a storyteller yet.</summary>
    public Guid? PromptSetId { get; set; }
    public PromptSet? PromptSet { get; set; }
    public Pacing Pacing { get; set; } = Pacing.Weekly;
    /// <summary>When weekly pacing started; prompt N unlocks N-1 weeks after this.</summary>
    public DateTime? PacingStartedAt { get; set; }

    public DateTime? InvitedAt { get; set; }
}

public sealed class AppRole : IdentityRole<Guid>
{
    public AppRole() { }
    public AppRole(string name) : base(name) { }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Storyteller = "Storyteller";
    public const string Family = "Family";
    public static readonly string[] All = [Admin, Storyteller, Family];
}

public enum MagicLinkPurpose
{
    /// <summary>Requested from the login page; emailed to the user.</summary>
    Login = 1,
    /// <summary>Printed on a QR login card by an admin.</summary>
    DeviceEnrollment = 2,
    /// <summary>Sent in an invitation email.</summary>
    Invitation = 3,
    /// <summary>The "Answer this question" button in a reminder email.</summary>
    Reminder = 4,
}

/// <summary>A single-use sign-in link. Only the SHA-256 hash of the token is stored.</summary>
public sealed class MagicLinkToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public required byte[] TokenHash { get; set; }
    public MagicLinkPurpose Purpose { get; set; }
    /// <summary>Local path to land on after sign-in (validated as local on use).</summary>
    public string? ReturnPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public string? CreatedByIp { get; set; }
    public string? ConsumedByIp { get; set; }
}

/// <summary>
/// A browser that signed in with a magic link and is remembered for 180 days (sliding).
/// The auth cookie carries the device id; revoking the row signs that browser out.
/// </summary>
public sealed class UserDevice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string? UserAgent { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
