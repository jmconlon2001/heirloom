using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Stories;

public sealed class DoneModel(AppDbContext db, StoryAccess access, StoryPlanService plans) : PageModel
{
    public Guid Id { get; private set; }
    public Guid StorytellerId { get; private set; }
    public string StorytellerName { get; private set; } = "";
    public bool ForOther { get; private set; }
    public int SharedCount { get; private set; }
    public PromptSlot? Next { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var memory = await db.Memories.AsNoTracking().Include(m => m.Storyteller).SingleOrDefaultAsync(m => m.Id == id);
        if (memory is null || !await access.CanEditAsync(User, memory)) return NotFound();
        Id = id;
        StorytellerId = memory.StorytellerId;
        StorytellerName = memory.Storyteller!.DisplayName;
        ForOther = memory.StorytellerId != User.UserId();
        var plan = await plans.GetAsync(memory.StorytellerId);
        SharedCount = plan.SharedCount;
        Next = plan.Next;
        return Page();
    }
}
