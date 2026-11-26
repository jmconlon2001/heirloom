using System.Text;
using FamilyMemories.Admin;
using FamilyMemories.Family;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Admin;

public sealed class GedcomModel(GedcomImporter importer, AuditLog audit) : PageModel
{
    public ImportPreview? Preview { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var text = await importer.ExportAsync();
        await audit.WriteAsync("gedcom.export", "Downloaded the family tree as GEDCOM");
        return File(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", $"family-tree-{DateTime.UtcNow:yyyy-MM-dd}.ged");
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0) { Error = "Please choose a .ged file first."; return Page(); }
        if (file.Length > GedcomImporter.MaxFileBytes) { Error = "That file is too big for a family tree (over 20 MB)."; return Page(); }
        await using var stream = file.OpenReadStream();
        Preview = await importer.PreviewAsync(stream);
        if (Preview.Rows.Count == 0) { Preview = null; Error = "We couldn't find any people in that file. Is it a GEDCOM (.ged) file?"; }
        return Page();
    }

    public async Task<IActionResult> OnPostCommitAsync(Guid fileId, Dictionary<string, string> decisions, bool mainTree)
    {
        var result = await importer.CommitAsync(fileId, decisions, mainTree);
        TempData["Flash"] = result is null
            ? "That import has expired. Please upload the file again."
            : mainTree
                ? $"Updated from the main tree: {result.Updated} people updated, {result.Created} new, {result.Skipped} skipped, {result.Relationships} family connections, {result.Facts} life events and notes."
                : $"Imported: {result.Created} new people, {result.Matched} matched to people already in the tree, {result.Skipped} skipped, {result.Relationships} family connections, {result.Facts} life events and notes.";
        return LocalRedirect(result is null ? "/admin/gedcom" : "/people");
    }
}
