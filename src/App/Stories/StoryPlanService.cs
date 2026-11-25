using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Stories;

public sealed record PromptSlot(Prompt Prompt, Memory? Memory, bool Unlocked, DateTime? UnlocksAt, bool IsOneOff)
{
    public MemoryStatus Status => Memory?.Status ?? MemoryStatus.NotStarted;
    public bool IsDone => Status is MemoryStatus.Complete or MemoryStatus.Declined;
}

public sealed record ChapterProgress(Guid? ChapterId, string Title, int Total, int Shared, IReadOnlyList<PromptSlot> Slots)
{
    public bool IsFinished => Slots.All(s => s.IsDone);
}

public sealed record StoryPlan(
    AppUser Storyteller,
    PromptSet? Set,
    IReadOnlyList<PromptSlot> Slots,
    IReadOnlyList<ChapterProgress> Chapters,
    int SharedCount,
    PromptSlot? Next)
{
    public bool HasSet => Set is not null;
    /// <summary>Every assigned prompt is finished or declined (reminders stop; Phase 4).</summary>
    public bool IsComplete => Slots.Count > 0 && Slots.All(s => s.IsDone);
    public PromptSlot? NextLocked => Slots.Where(s => !s.Unlocked).MinBy(s => s.UnlocksAt);
}

/// <summary>
/// Works out which prompts a storyteller has, which are unlocked under their pacing,
/// where they are with each, and what to suggest next.
/// </summary>
public sealed class StoryPlanService(AppDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public async Task<StoryPlan> GetAsync(Guid storytellerId)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.PromptSet).SingleAsync(u => u.Id == storytellerId);
        var setPrompts = await SetPromptsAsync(user.PromptSet);
        var oneOffs = await db.Prompts.AsNoTracking().Include(p => p.AskedByUser)
            .Where(p => p.ForUserId == storytellerId && p.IsActive)
            .OrderBy(p => p.CreatedAt).ToListAsync();

        var memories = await db.Memories.AsNoTracking()
            .Where(m => m.StorytellerId == storytellerId)
            .ToListAsync();
        var byPrompt = memories.Where(m => m.PromptId != null).ToDictionary(m => m.PromptId!.Value);

        var now = clock.GetUtcNow().UtcDateTime;
        var start = user.PacingStartedAt ?? now;
        var unlockedCount = user.Pacing == Pacing.AllAtOnce ? int.MaxValue : (int)Math.Floor((now - start) / Week) + 1;

        var slots = new List<PromptSlot>();
        foreach (var p in oneOffs)
            slots.Add(new PromptSlot(p, byPrompt.GetValueOrDefault(p.Id), true, null, IsOneOff: true));
        for (var i = 0; i < setPrompts.Count; i++)
        {
            var p = setPrompts[i];
            var memory = byPrompt.GetValueOrDefault(p.Id);
            // Anything already started stays open, whatever the pacing says.
            var unlocked = i < unlockedCount || (memory is not null && memory.Status != MemoryStatus.NotStarted);
            slots.Add(new PromptSlot(p, memory, unlocked, unlocked ? null : start + Week * i, IsOneOff: false));
        }

        var chapters = slots
            .GroupBy(s => s.IsOneOff ? (Guid?)null : s.Prompt.ChapterId)
            .Select(g => new ChapterProgress(
                g.Key,
                g.Key is null ? "Asked just for you" : g.First().Prompt.Chapter?.Title ?? "",
                g.Count(),
                g.Count(s => s.Status == MemoryStatus.Complete),
                [.. g]))
            .ToList();

        var shared = memories.Count(m => m.Status == MemoryStatus.Complete);
        return new StoryPlan(user, user.PromptSet, slots, chapters, shared, PickNext(slots));
    }

    /// <summary>One-offs first; then the first unlocked prompt not yet answered; skipped ones come back last.</summary>
    internal static PromptSlot? PickNext(IReadOnlyList<PromptSlot> slots) =>
        slots.FirstOrDefault(s => s.Unlocked && s.Status is MemoryStatus.NotStarted or MemoryStatus.Draft)
        ?? slots.FirstOrDefault(s => s.Unlocked && s.Status == MemoryStatus.Skipped);

    public async Task<List<Prompt>> SetPromptsAsync(PromptSet? set)
    {
        if (set is null) return [];
        var query = db.Prompts.AsNoTracking().Include(p => p.Chapter)
            .Where(p => p.IsActive && p.ForUserId == null && p.ChapterId != null);
        query = set.Kind switch
        {
            PromptSetKind.Core => query.Where(p => p.IsCore),
            PromptSetKind.Full => query,
            _ => query.Where(p => db.PromptSetItems.Any(i => i.PromptSetId == set.Id && i.PromptId == p.Id)),
        };
        return await query.OrderBy(p => p.Chapter!.SortOrder).ThenBy(p => p.SortOrder).ToListAsync();
    }
}
