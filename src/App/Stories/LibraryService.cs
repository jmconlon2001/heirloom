using System.Security.Claims;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Stories;

public sealed record StoryCard(Memory Memory, string StorytellerName, Guid? ThumbAttachmentId, int Photos, int Recordings, int Hearts)
{
    public string Excerpt
    {
        get => StoryMarkup.Excerpt(Memory.Body);
    }
}

/// <summary>Read-side queries for the Family Library. Everything starts from <see cref="StoryAccess.Library"/>.</summary>
public sealed class LibraryService(AppDbContext db, StoryAccess access)
{
    public IQueryable<Memory> Visible(ClaimsPrincipal user) => access.Library(user).AsNoTracking();

    public async Task<List<StoryCard>> CardsAsync(IQueryable<Memory> query)
    {
        var rows = await query.Select(m => new
        {
            Memory = m,
            Teller = m.Storyteller!.DisplayName,
            Thumb = db.Attachments.Where(a => a.MemoryId == m.Id && a.RemovedAt == null && a.State == ProcessingState.Ready
                                              && (a.Kind == AttachmentKind.Photo || a.Kind == AttachmentKind.Video))
                .OrderBy(a => a.SortOrder).Select(a => (Guid?)a.Id).FirstOrDefault(),
            Photos = db.Attachments.Count(a => a.MemoryId == m.Id && a.RemovedAt == null && a.Kind == AttachmentKind.Photo),
            Recordings = db.Attachments.Count(a => a.MemoryId == m.Id && a.RemovedAt == null && a.Kind == AttachmentKind.Audio),
            Hearts = db.Reactions.Count(r => r.MemoryId == m.Id),
        }).ToListAsync();
        return [.. rows.Select(r => new StoryCard(r.Memory, r.Teller, r.Thumb, r.Photos, r.Recordings, r.Hearts))];
    }

    public Task<List<StoryCard>> NewestAsync(ClaimsPrincipal user, int count) =>
        CardsAsync(Visible(user).OrderByDescending(m => m.CompletedAt ?? m.UpdatedAt).Take(count));

    /// <summary>Postgres full-text search over titles, words, places, captions and the names of people in the story.</summary>
    public Task<List<StoryCard>> SearchAsync(ClaimsPrincipal user, string q)
    {
        var like = $"%{q.Trim().Replace("%", "").Replace("_", "")}%";
        var query = Visible(user).Where(m =>
            EF.Functions.ToTsVector("english", m.Title + " " + m.Body + " " + (m.Place ?? ""))
                .Matches(EF.Functions.WebSearchToTsQuery("english", q))
            || EF.Functions.ILike(m.Title, like)
            || EF.Functions.ILike(m.Place ?? "", like)
            || db.Attachments.Any(a => a.MemoryId == m.Id && a.RemovedAt == null && EF.Functions.ILike(a.Caption ?? "", like))
            || db.MemoryPeople.Any(x => x.MemoryId == m.Id && EF.Functions.ILike(x.Person!.GivenNames + " " + x.Person.Surname, like)));
        return CardsAsync(query.OrderByDescending(m => m.CompletedAt).Take(100));
    }

    /// <summary>Every photo in stories this person can read, newest stories first.</summary>
    public IQueryable<Attachment> Photos(ClaimsPrincipal user)
    {
        var memories = Visible(user).Select(m => m.Id);
        return db.Attachments.AsNoTracking().Include(a => a.Memory)
            .Where(a => a.MemoryId != null && memories.Contains(a.MemoryId.Value) && a.RemovedAt == null && a.Kind == AttachmentKind.Photo && a.State == ProcessingState.Ready)
            .OrderByDescending(a => a.Memory!.CompletedAt).ThenBy(a => a.SortOrder);
    }
}
