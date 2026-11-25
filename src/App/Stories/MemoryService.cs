using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyMemories.Stories;

public sealed record MemoryEdit(string? Title, string? Body, FuzzyDate When, string? Place);

public enum SaveOutcome { Saved, Unchanged, Conflict, NotFound }

public sealed record SaveResult(SaveOutcome Outcome, int Revision, DateTime SavedAt);

/// <summary>Creating, saving, versioning and finishing memories.</summary>
public sealed class MemoryService(AppDbContext db, AppOptions options, AuditLog audit, TimeProvider clock)
{
    public const int MaxBodyLength = 200_000;
    /// <summary>Autosaves by the same person within this window update one version instead of adding more.</summary>
    public static readonly TimeSpan AutosaveCoalesceWindow = TimeSpan.FromMinutes(30);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Memory> GetOrCreateForPromptAsync(Guid storytellerId, Prompt prompt, Guid writerId)
    {
        var existing = await db.Memories.SingleOrDefaultAsync(m => m.StorytellerId == storytellerId && m.PromptId == prompt.Id);
        if (existing is not null) return existing;

        var memory = NewMemory(storytellerId, writerId, prompt.Title);
        memory.PromptId = prompt.Id;
        db.Memories.Add(memory);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Opened in two tabs at once: the other request created it first.
            db.Entry(memory).State = EntityState.Detached;
            return await db.Memories.SingleAsync(m => m.StorytellerId == storytellerId && m.PromptId == prompt.Id);
        }
        return memory;
    }

    public async Task<Memory> CreateFreeFormAsync(Guid storytellerId, Guid writerId)
    {
        var memory = NewMemory(storytellerId, writerId, "");
        db.Memories.Add(memory);
        await db.SaveChangesAsync();
        return memory;
    }

    private Memory NewMemory(Guid storytellerId, Guid writerId, string title)
    {
        var now = Now;
        var memory = new Memory
        {
            Id = Guid.CreateVersion7(),
            StorytellerId = storytellerId,
            Title = title,
            Status = MemoryStatus.NotStarted,
            Visibility = options.DefaultVisibility,
            CreatedAt = now,
            UpdatedAt = now,
            WrittenById = writerId == storytellerId ? null : writerId,
        };
        if (memory.WrittenById is not null)
            audit.Add("memory.proxy-start", $"Started a story on behalf of storyteller {storytellerId}", "memory", memory.Id);
        return memory;
    }

    /// <summary>
    /// Applies typed changes. <paramref name="expectedRevision"/> (from autosave) guards against an
    /// old tab overwriting newer words; the Save button passes null and wins.
    /// </summary>
    public async Task<SaveResult> SaveAsync(Guid memoryId, MemoryEdit edit, Guid actorId, VersionKind kind, int? expectedRevision = null)
    {
        var memory = await db.Memories.SingleOrDefaultAsync(m => m.Id == memoryId);
        if (memory is null) return new(SaveOutcome.NotFound, 0, default);
        if (expectedRevision is { } expected && expected != memory.Revision)
            return new(SaveOutcome.Conflict, memory.Revision, memory.UpdatedAt);

        var title = Clean(edit.Title, 300);
        var body = NormalizeBody(edit.Body);
        var place = Clean(edit.Place, 300);
        if (string.IsNullOrEmpty(title) && memory.PromptId is not null)
            title = await db.Prompts.Where(p => p.Id == memory.PromptId).Select(p => p.Title).SingleAsync();

        var changed = memory.Title != (title ?? "") || memory.Body != body || memory.Place != place || memory.When != edit.When;
        if (!changed && kind == VersionKind.Autosave)
            return new(SaveOutcome.Unchanged, memory.Revision, memory.UpdatedAt);

        memory.Title = title ?? "";
        memory.Body = body;
        memory.Place = place;
        memory.When = edit.When;
        if (memory.Status is MemoryStatus.NotStarted or MemoryStatus.Skipped or MemoryStatus.Declined
            && (body.Length > 0 || kind == VersionKind.Save))
            memory.Status = MemoryStatus.Draft;

        TrackProxyWriter(memory, actorId);
        await AddVersionAsync(memory, actorId, kind);
        return await CommitAsync(memory);
    }

    public async Task<bool> SetStatusAsync(Guid memoryId, MemoryStatus status, Guid actorId)
    {
        var memory = await db.Memories.SingleOrDefaultAsync(m => m.Id == memoryId);
        if (memory is null) return false;

        memory.Status = status;
        memory.CompletedAt = status == MemoryStatus.Complete ? Now : memory.CompletedAt;
        TrackProxyWriter(memory, actorId);
        if (status == MemoryStatus.Complete) await AddVersionAsync(memory, actorId, VersionKind.Save);
        await CommitAsync(memory);
        return true;
    }

    public async Task<bool> RestoreVersionAsync(Guid memoryId, int number, Guid actorId)
    {
        var memory = await db.Memories.SingleOrDefaultAsync(m => m.Id == memoryId);
        var version = await db.MemoryVersions.AsNoTracking().SingleOrDefaultAsync(v => v.MemoryId == memoryId && v.Number == number);
        if (memory is null || version is null) return false;

        memory.Title = version.Title;
        memory.Body = version.Body;
        memory.Place = version.Place;
        memory.When = new FuzzyDate(version.WhenYear, version.WhenMonth, version.WhenDay, version.WhenCirca, version.WhenDecade, version.WhenLabel);
        await AddVersionAsync(memory, actorId, VersionKind.Restore, force: true);
        audit.Add("memory.restore", $"Restored version {number} of \"{memory.Title}\"", "memory", memory.Id);
        await CommitAsync(memory);
        return true;
    }

    private void TrackProxyWriter(Memory memory, Guid actorId)
    {
        if (actorId != memory.StorytellerId && memory.WrittenById is null)
        {
            memory.WrittenById = actorId;
            audit.Add("memory.proxy-write", $"Wrote on behalf of storyteller {memory.StorytellerId}", "memory", memory.Id);
        }
    }

    private async Task AddVersionAsync(Memory memory, Guid actorId, VersionKind kind, bool force = false)
    {
        var now = Now;
        var latest = await db.MemoryVersions.Where(v => v.MemoryId == memory.Id)
            .OrderByDescending(v => v.Number).FirstOrDefaultAsync();

        if (!force && latest is not null && SameContent(latest, memory))
        {
            // Nothing new to keep; promote a pending autosave to a real save if needed.
            if (kind == VersionKind.Save && latest.Kind == VersionKind.Autosave) latest.Kind = VersionKind.Save;
            return;
        }

        if (kind == VersionKind.Autosave && latest is { Kind: VersionKind.Autosave } recent
            && recent.SavedById == actorId && now - recent.SavedAt < AutosaveCoalesceWindow)
        {
            Snapshot(recent, memory);
            recent.SavedAt = now;
            return;
        }

        var version = new MemoryVersion
        {
            Id = Guid.CreateVersion7(),
            MemoryId = memory.Id,
            Number = (latest?.Number ?? 0) + 1,
            Kind = kind,
            SavedById = actorId,
            SavedAt = now,
        };
        Snapshot(version, memory);
        db.MemoryVersions.Add(version);
    }

    private static void Snapshot(MemoryVersion v, Memory m)
    {
        v.Title = m.Title; v.Body = m.Body; v.Status = m.Status; v.Place = m.Place;
        v.WhenYear = m.WhenYear; v.WhenMonth = m.WhenMonth; v.WhenDay = m.WhenDay;
        v.WhenCirca = m.WhenCirca; v.WhenDecade = m.WhenDecade; v.WhenLabel = m.WhenLabel;
    }

    private static bool SameContent(MemoryVersion v, Memory m) =>
        v.Title == m.Title && v.Body == m.Body && v.Place == m.Place
        && v.WhenYear == m.WhenYear && v.WhenMonth == m.WhenMonth && v.WhenDay == m.WhenDay
        && v.WhenCirca == m.WhenCirca && v.WhenDecade == m.WhenDecade && v.WhenLabel == m.WhenLabel;

    private async Task<SaveResult> CommitAsync(Memory memory)
    {
        memory.UpdatedAt = Now;
        memory.Revision++;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(SaveOutcome.Conflict, memory.Revision - 1, memory.UpdatedAt);
        }
        return new(SaveOutcome.Saved, memory.Revision, memory.UpdatedAt);
    }

    internal static string NormalizeBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        body = body.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        return body.Length > MaxBodyLength ? body[..MaxBodyLength] : body;
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
