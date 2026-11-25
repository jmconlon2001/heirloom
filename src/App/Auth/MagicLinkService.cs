using System.Security.Cryptography;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Auth;

public enum MagicLinkFailure { None, NotFound, Expired, AlreadyUsed, UserDisabled }

public sealed record MagicLinkRedemption(AppUser? User, string? ReturnPath, MagicLinkFailure Failure)
{
    public bool Succeeded => Failure == MagicLinkFailure.None && User is not null;
}

/// <summary>
/// Issues and redeems single-use sign-in links. Tokens are 256 random bits, sent only in the
/// link; the database stores their SHA-256 hash, so a leaked database cannot be used to sign in.
/// </summary>
public sealed class MagicLinkService(AppDbContext db, AppOptions options, TimeProvider clock)
{
    /// <summary>At most this many login links per user per hour, so nobody can flood an inbox.</summary>
    public const int MaxLinksPerUserPerHour = 5;

    public async Task<string?> IssueAsync(AppUser user, MagicLinkPurpose purpose, string? returnPath, string? ip,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        if (purpose == MagicLinkPurpose.Login)
        {
            var recent = await db.MagicLinkTokens.CountAsync(
                t => t.UserId == user.Id && t.Purpose == MagicLinkPurpose.Login && t.CreatedAt > now.AddHours(-1), ct);
            if (recent >= MaxLinksPerUserPerHour) return null;
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        db.MagicLinkTokens.Add(new MagicLinkToken
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = Hash(token),
            Purpose = purpose,
            ReturnPath = returnPath,
            CreatedAt = now,
            ExpiresAt = now.Add(options.MagicLinkLifetime),
            CreatedByIp = ip,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    public Uri BuildUrl(string token) => options.BuildUrl($"account/magic-link?token={Uri.EscapeDataString(token)}");

    /// <summary>Checks a token without using it up (for the "Sign me in" confirmation page).</summary>
    public async Task<MagicLinkFailure> PeekAsync(string? token, CancellationToken ct = default)
    {
        var row = await FindAsync(token, ct);
        return Check(row);
    }

    /// <summary>Atomically marks the token used. Exactly one concurrent caller can succeed.</summary>
    public async Task<MagicLinkRedemption> RedeemAsync(string? token, string? ip, CancellationToken ct = default)
    {
        var row = await FindAsync(token, ct);
        var failure = Check(row);
        if (failure != MagicLinkFailure.None) return new(null, null, failure);

        var now = clock.GetUtcNow().UtcDateTime;
        var updated = await db.MagicLinkTokens
            .Where(t => t.Id == row!.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now).SetProperty(t => t.ConsumedByIp, ip), ct);
        if (updated != 1) return new(null, null, MagicLinkFailure.AlreadyUsed);

        return new(row!.User, row.ReturnPath, MagicLinkFailure.None);
    }

    private async Task<MagicLinkToken?> FindAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        var hash = Hash(token);
        return await db.MagicLinkTokens.Include(t => t.User).AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
    }

    private MagicLinkFailure Check(MagicLinkToken? row)
    {
        if (row?.User is null) return MagicLinkFailure.NotFound;
        if (row.ConsumedAt is not null) return MagicLinkFailure.AlreadyUsed;
        if (row.ExpiresAt <= clock.GetUtcNow().UtcDateTime) return MagicLinkFailure.Expired;
        if (row.User.LockoutEnd is { } end && end > clock.GetUtcNow()) return MagicLinkFailure.UserDisabled;
        return MagicLinkFailure.None;
    }

    internal static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}

internal static class WebEncoders
{
    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
