using FamilyMemories.Archive;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library;

public sealed class PhotosModel(LibraryService library) : PageModel
{
    public List<Attachment> Photos { get; private set; } = [];
    public IReadOnlyList<KeywordNode> Keywords { get; private set; } = [];
    public string? Keyword { get; private set; }

    public async Task OnGetAsync(string? k)
    {
        var all = await library.Photos(User).Take(2000).ToListAsync();
        Keywords = ArchiveService.BuildKeywordTree(all.Where(a => a.Keywords.Count > 0).Select(a => a.Keywords));
        Keyword = string.IsNullOrWhiteSpace(k) ? null : k.Trim();
        Photos = [.. (Keyword is null ? all : all.Where(a => ArchiveService.HasKeyword(a, Keyword))).Take(500)];
    }
}
