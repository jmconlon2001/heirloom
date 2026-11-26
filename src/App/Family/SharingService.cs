using System.Security.Cryptography;
using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Family;

public enum LinkFailure { None, NotFound, Revoked, Expired, NotShared }

public sealed record LinkResolution(LinkShare? Share, LinkFailure Failure)
{
    public bool Ok => Failure == LinkFailure.None;
}

/// <summary>Who can see a story, and read-only links for relatives without accounts.</summary>
public sealed class SharingService(AppDbContext db, AuditLog audit, AppOptions options, IDataProtectionProvider protection, TimeProvider clock)
{
    private static readonly PasswordHasher<LinkShare> Hasher = new();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private ITimeLimitedDataProtector UnlockProtector => protection.CreateProtector("link-unlock").ToTimeLimitedDataProtector();

    public async Task SetVisibilityAsync(Guid memoryId, Visibility visibility, IEnumerable<Guid> sharedWith)
    {
        var memory = await db.Memories.SingleAsync(m => m.Id == memoryId);
        memory.Visibility = visibility;
        var wanted = visibility == Visibility.SelectedPeople ? sharedWith.Distinct().ToHashSet() : [];
        var current = await db.MemoryShares.Where(s => s.MemoryId == memoryId).ToListAsync();
        db.MemoryShares.RemoveRange(current.Where(s => !wanted.Contains(s.UserId)));
        foreach (var id in wanted.Where(id => current.All(c => c.UserId != id)))
            db.MemoryShares.Add(new MemoryShare { MemoryId = memoryId, UserId = id });
        await db.SaveChangesAsync();
    }

    public async Task<string> CreateLinkAsync(Guid memoryId, Guid createdBy, TimeSpan? lifetime, string? passcode, string? label)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var share = new LinkShare
        {
            Id = Guid.CreateVersion7(),
            MemoryId = memoryId,
            TokenHash = MagicLinkService.Hash(token),
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 200)],
            ExpiresAt = lifetime is null ? null : Now.Add(lifetime.Value),
            CreatedById = createdBy,
            CreatedAt = Now,
        };
        if (!string.IsNullOrWhiteSpace(passcode)) share.PasscodeHash = Hasher.HashPassword(share, passcode.Trim());
        db.LinkShares.Add(share);
        audit.Add("link.create", $"Created a share link{(share.PasscodeHash is null ? "" : " with a passcode")} for story {memoryId}", "memory", memoryId);
        await db.SaveChangesAsync();
        return token;
    }

    public Uri LinkUrl(string token) => options.BuildUrl($"s/{token}");

    /// <summary>A link to one recording or video, for the QR codes printed in PDFs and books.</summary>
    public async Task<string> CreateClipLinkAsync(Attachment clip, Guid createdBy, string label)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        db.LinkShares.Add(new LinkShare
        {
            Id = Guid.CreateVersion7(), MemoryId = clip.MemoryId!.Value, AttachmentId = clip.Id,
            TokenHash = MagicLinkService.Hash(token), Label = label, CreatedById = createdBy, CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        return token;
    }

    public async Task RevokeAsync(Guid memoryId, Guid shareId)
    {
        var share = await db.LinkShares.SingleOrDefaultAsync(s => s.Id == shareId && s.MemoryId == memoryId && s.RevokedAt == null);
        if (share is null) return;
        share.RevokedAt = Now;
        audit.Add("link.revoke", $"Turned off a share link for story {memoryId}", "memory", memoryId);
        await db.SaveChangesAsync();
    }

    /// <summary>A link works while it isn't revoked or expired and the story is finished and still set to "Link".</summary>
    public async Task<LinkResolution> ResolveAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return new(null, LinkFailure.NotFound);
        var hash = MagicLinkService.Hash(token);
        var share = await db.LinkShares.Include(s => s.Memory).SingleOrDefaultAsync(s => s.TokenHash == hash);
        if (share is null) return new(null, LinkFailure.NotFound);
        if (share.RevokedAt is not null) return new(share, LinkFailure.Revoked);
        if (share.ExpiresAt is { } exp && exp <= Now) return new(share, LinkFailure.Expired);
        if (share.AttachmentId is { } clipId)
            return await db.Attachments.AnyAsync(a => a.Id == clipId && a.RemovedAt == null)
                ? new(share, LinkFailure.None) : new(share, LinkFailure.NotFound);
        if (share.Memory!.Visibility != Visibility.Link || share.Memory.Status != MemoryStatus.Complete) return new(share, LinkFailure.NotShared);
        return new(share, LinkFailure.None);
    }

    public bool CheckPasscode(LinkShare share, string? passcode) =>
        share.PasscodeHash is null
        || (!string.IsNullOrWhiteSpace(passcode)
            && Hasher.VerifyHashedPassword(share, share.PasscodeHash, passcode.Trim()) != PasswordVerificationResult.Failed);

    /// <summary>After the right passcode, a cookie remembers it for this link for 12 hours.</summary>
    public string UnlockCookieName(LinkShare share) => $"fm_link_{share.Id:N}";
    public string CreateUnlockValue(LinkShare share) => UnlockProtector.Protect(share.Id.ToString(), TimeSpan.FromHours(12));

    public bool IsUnlocked(LinkShare share, string? cookie)
    {
        if (share.PasscodeHash is null) return true;
        if (string.IsNullOrEmpty(cookie)) return false;
        try { return UnlockProtector.Unprotect(cookie) == share.Id.ToString(); }
        catch (CryptographicException) { return false; }
    }

    public async Task RecordUseAsync(LinkShare share)
    {
        await db.LinkShares.Where(s => s.Id == share.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ViewCount, x => x.ViewCount + 1).SetProperty(x => x.LastUsedAt, Now));
    }
}
