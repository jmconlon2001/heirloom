using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Library;

public sealed class TimelineModel(LibraryService library) : PageModel
{
    public List<IGrouping<int, StoryCard>> Decades { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var cards = await library.CardsAsync(library.Visible(User));
        Decades = [.. cards
            .OrderBy(c => c.Memory.When.SortDate ?? DateOnly.MaxValue)
            .GroupBy(c => c.Memory.When.SortDate is { } d ? d.Year / 10 * 10 : 0)
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)];
    }
}
