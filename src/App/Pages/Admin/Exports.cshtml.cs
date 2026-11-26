using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Exports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin;

public sealed class ExportsModel(AppDbContext db, ExportService exports) : PageModel
{
    public List<ExportJob> Jobs { get; private set; } = [];
    public List<AppUser> Storytellers { get; private set; } = [];
    public List<Chapter> Chapters { get; private set; } = [];
    public List<Attachment> Photos { get; private set; } = [];
    public ExportKind Kind { get; private set; } = ExportKind.Pdf;
    public string? Error { get; private set; }

    private async Task LoadAsync()
    {
        Jobs = await db.Exports.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(30).ToListAsync();
        Storytellers = await db.Users.AsNoTracking().Where(u => db.Memories.Any(m => m.StorytellerId == u.Id && m.Status == MemoryStatus.Complete))
            .OrderBy(u => u.DisplayName).ToListAsync();
        Chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync();
        Photos = await db.Attachments.AsNoTracking().Where(a => a.Kind == AttachmentKind.Photo && a.State == ProcessingState.Ready && a.RemovedAt == null)
            .OrderByDescending(a => a.CreatedAt).Take(60).ToListAsync();
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync(ExportKind kind, List<Guid> storytellerIds, List<Guid> chapterIds, bool includeDrafts,
        bool includePrivate, bool confirmPrivate, string? title, string? subtitle, string? dedication, OrganizeBy organize,
        TrimSize trim, bool bleed, string? paper, int? pageMultiple, string? backText, Guid? frontPhotoId, Guid? backPhotoId)
    {
        if (includePrivate && !confirmPrivate)
        {
            await LoadAsync();
            Kind = kind;
            Error = "To include private stories, please also tick \"I understand private stories will be in the file\".";
            return Page();
        }
        var scope = kind == ExportKind.Archive ? new ExportScope() : new ExportScope
        {
            StorytellerIds = storytellerIds, ChapterIds = chapterIds, IncludePrivate = includePrivate,
            Statuses = includeDrafts ? [MemoryStatus.Complete, MemoryStatus.Draft] : [MemoryStatus.Complete],
        };
        var preset = paper is not null && SpineCalculator.Presets.TryGetValue(paper, out var p) ? (paper, p.PagesPerInch) : ("kdp-white", SpineCalculator.Presets["kdp-white"].PagesPerInch);
        var book = new BookOptions
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Our Family Stories" : title.Trim(),
            Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim(),
            Dedication = string.IsNullOrWhiteSpace(dedication) ? null : dedication.Trim(),
            Organize = organize, Trim = trim, Bleed = bleed, PaperPreset = preset.Item1, PagesPerInch = preset.Item2,
            PageMultiple = Math.Clamp(pageMultiple ?? 2, 1, 32), BackText = string.IsNullOrWhiteSpace(backText) ? null : backText.Trim(),
            FrontPhotoId = frontPhotoId, BackPhotoId = backPhotoId,
        };
        await exports.StartAsync(kind, scope, book, User.UserId());
        TempData["Flash"] = "Started. You'll get an email when it's ready; you can also watch it here.";
        return LocalRedirect("/admin/exports");
    }
}
