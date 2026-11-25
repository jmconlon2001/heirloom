using FamilyMemories.Auth;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages;

/// <summary>
/// The stable deep link for a question (used by reminder emails): finds or starts the
/// storyteller's story for it and opens the writing page.
/// </summary>
public sealed class AnswerModel(StoryPlanService plans, StoryAccess access, MemoryService memories) : PageModel
{
    public async Task<IActionResult> OnGetAsync(Guid promptId, Guid? @for)
    {
        var me = User.UserId();
        var storytellerId = @for ?? me;
        if (!await access.CanWriteForAsync(User, storytellerId)) return NotFound();

        var plan = await plans.GetAsync(storytellerId);
        var slot = plan.Slots.FirstOrDefault(s => s.Prompt.Id == promptId);
        if (slot is null) return NotFound();
        if (!slot.Unlocked)
        {
            TempData["Flash"] = $"That question opens on {slot.UnlocksAt:MMMM d}. Here is what's ready now.";
            return LocalRedirect(storytellerId == me ? "/" : $"/questions?for={storytellerId}");
        }

        var memory = slot.Memory ?? await memories.GetOrCreateForPromptAsync(storytellerId, slot.Prompt, me);
        return LocalRedirect($"/stories/{memory.Id}/write");
    }
}
