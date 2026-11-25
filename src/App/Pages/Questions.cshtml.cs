using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages;

public sealed class QuestionsModel(StoryPlanService plans, StoryAccess access) : PageModel
{
    public StoryPlan Plan { get; private set; } = null!;
    public AppUser Storyteller => Plan.Storyteller;
    public bool ForOther { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? @for)
    {
        var storytellerId = @for ?? User.UserId();
        if (!await access.CanWriteForAsync(User, storytellerId)) return NotFound();
        Plan = await plans.GetAsync(storytellerId);
        ForOther = storytellerId != User.UserId();
        return Page();
    }
}
