using System.Text;
using FamilyMemories.Imports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Admin.Import;

public sealed class StoriesModel(StoriesCsvImporter importer) : PageModel
{
    public StoryImportPreview? Preview { get; private set; }
    public StoryImportResult? Result { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public IActionResult OnGetTemplate() =>
        File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(StoriesCsvImporter.Template())).ToArray(), "text/csv; charset=utf-8", "stories.csv");

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0) { Error = "Please choose a spreadsheet file first (saved as CSV)."; return Page(); }
        if (file.Length > StoriesCsvImporter.MaxFileBytes) { Error = "That file is too big (over 20 MB). Is it the right one?"; return Page(); }
        await using var stream = file.OpenReadStream();
        Preview = await importer.PreviewAsync(stream);
        if (Preview.Rows.Count == 0)
        {
            Preview = null;
            Error = "We couldn't find any stories in that file. Save the spreadsheet as CSV, with the column names (like Storyteller and Story) in the first row.";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCommitAsync(Guid fileId, Dictionary<string, string> decisions)
    {
        Result = await importer.CommitAsync(fileId, decisions);
        if (Result is null) Error = "That import has expired. Please upload the file again.";
        return Page();
    }
}
