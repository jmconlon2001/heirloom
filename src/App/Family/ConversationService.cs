using FamilyMemories.Admin;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Family;

/// <summary>♥ reactions, short comments, and "Ask a follow-up" questions on shared stories.</summary>
public sealed class ConversationService(AppDbContext db, AuditLog audit, TimeProvider clock)
{
    public const int MaxCommentLength = 2000;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<bool> ToggleHeartAsync(Guid memoryId, Guid userId)
    {
        var existing = await db.Reactions.SingleOrDefaultAsync(r => r.MemoryId == memoryId && r.UserId == userId);
        if (existing is null) db.Reactions.Add(new MemoryReaction { MemoryId = memoryId, UserId = userId, CreatedAt = Now });
        else db.Reactions.Remove(existing);
        await db.SaveChangesAsync();
        return existing is null;
    }

    public async Task<MemoryComment?> AddCommentAsync(Guid memoryId, Guid userId, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var text = body.Trim().Replace("\r\n", "\n");
        var comment = new MemoryComment
        {
            Id = Guid.CreateVersion7(), MemoryId = memoryId, UserId = userId,
            Body = text.Length > MaxCommentLength ? text[..MaxCommentLength] : text, CreatedAt = Now,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        return comment;
    }

    /// <summary>Authors can remove their own comments; admins and the storyteller can remove any on their story.</summary>
    public async Task<bool> DeleteCommentAsync(Guid commentId, Guid userId, bool isAdmin)
    {
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == commentId && c.DeletedAt == null);
        if (comment is null) return false;
        var storyteller = await db.Memories.Where(m => m.Id == comment.MemoryId).Select(m => m.StorytellerId).SingleAsync();
        if (comment.UserId != userId && !isAdmin && storyteller != userId) return false;
        comment.DeletedAt = Now;
        if (comment.UserId != userId) audit.Add("comment.remove", "Removed someone else's comment", "memory", comment.MemoryId);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Creates a question just for the storyteller ("Lily asked: …"); emailed on the next reminder (Phase 4).</summary>
    public async Task<Prompt?> AskFollowUpAsync(Memory memory, Guid askerId, string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || askerId == memory.StorytellerId) return null;
        var text = question.Trim();
        if (text.Length > 2000) text = text[..2000];
        var prompt = new Prompt
        {
            Id = Guid.CreateVersion7(),
            ForUserId = memory.StorytellerId,
            AskedByUserId = askerId,
            FollowUpOfMemoryId = memory.Id,
            Title = text.Length > 80 ? text[..79].TrimEnd() + "…" : text,
            Question = text,
            Hints = [$"This follows on from your story \"{memory.Title}\"."],
            IsActive = true,
            CreatedAt = Now,
        };
        db.Prompts.Add(prompt);
        await db.SaveChangesAsync();
        return prompt;
    }
}
