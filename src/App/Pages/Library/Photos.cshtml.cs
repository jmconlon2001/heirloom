using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library;

public sealed class PhotosModel(LibraryService library) : PageModel
{
    public List<Attachment> Photos { get; private set; } = [];
    public async Task OnGetAsync() => Photos = await library.Photos(User).Take(500).ToListAsync();
}
