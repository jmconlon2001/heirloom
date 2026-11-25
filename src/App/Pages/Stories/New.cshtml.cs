using FamilyMemories.Auth;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Stories;

/// <summary>"Add a story of your own": a story that doesn't answer any particular question.</summary>
public sealed class NewModel(StoryAccess access, MemoryService memories) : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");

    public async Task<IActionResult> OnPostAsync(Guid? @for)
    {
        var storytellerId = @for ?? User.UserId();
        if (!await access.CanWriteForAsync(User, storytellerId)) return NotFound();
        var memory = await memories.CreateFreeFormAsync(storytellerId, User.UserId());
        return LocalRedirect($"/stories/{memory.Id}/write");
    }
}
