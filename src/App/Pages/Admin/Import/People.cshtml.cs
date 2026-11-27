using System.Text;
using FamilyMemories.Auth;
using FamilyMemories.Imports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Admin.Import;

/// <summary>
/// The check-before-importing form sends one choice per row; a real family tree (1,400 people from Reunion) is well past
/// ASP.NET Core's default limit of 1,024 form values.
/// </summary>
[RequestFormLimits(ValueCountLimit = 100_000)]
public sealed class PeopleModel(PeopleCsvImporter importer) : PageModel
{
    public PeopleImportPreview? Preview { get; private set; }
    public PeopleImportResult? Result { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public IActionResult OnGetTemplate() =>
        File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(PeopleCsvImporter.Template())).ToArray(), "text/csv; charset=utf-8", "people.csv");

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0) { Error = "Please choose a spreadsheet file first (saved as CSV)."; return Page(); }
        if (file.Length > PeopleCsvImporter.MaxFileBytes) { Error = "That file is too big (over 10 MB). Is it the right one?"; return Page(); }
        await using var stream = file.OpenReadStream();
        Preview = await importer.PreviewAsync(stream);
        if (Preview.Rows.Count == 0)
        {
            Preview = null;
            Error = "We couldn't find anyone in that file. Save the spreadsheet as CSV, with the column names (like First name and Last name) in the first row.";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCommitAsync(Guid fileId, Dictionary<string, string> decisions, bool sendInvitations)
    {
        Result = await importer.CommitAsync(fileId, decisions, sendInvitations, User.DisplayName());
        if (Result is null) Error = "That import has expired. Please upload the file again.";
        return Page();
    }
}
