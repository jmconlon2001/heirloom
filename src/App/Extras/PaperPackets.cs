using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Exports;
using FamilyMemories.Stories;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Extras;

/// <summary>
/// Paper packets (Phase 6): for storytellers who won't use a device. The questions waiting for them
/// (up to 12), one per page with writing lines. An admin scans the pages back and adds them through
/// "Write a story for …".
/// </summary>
public sealed class PaperPackets(AppDbContext db, StoryPlanService plans, RazorRenderer razor, GotenbergClient gotenberg, AuditLog audit, AppOptions options)
{
    public const int MaxQuestions = 12;

    public async Task<(byte[] Pdf, int Questions)> MakeAsync(Guid storytellerId, string helperName, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == storytellerId, ct);
        var plan = await plans.GetAsync(storytellerId);
        var questions = plan.Slots
            .Where(s => s.Unlocked && s.Status is MemoryStatus.NotStarted or MemoryStatus.Draft or MemoryStatus.Skipped)
            .Select(s => s.Prompt).Take(MaxQuestions).ToList();
        if (questions.Count == 0) questions = [.. plan.Slots.Where(s => !s.IsDone).Select(s => s.Prompt).Take(MaxQuestions)];

        var dir = Path.Combine(options.DataDir, "exports", $"packet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var html = await razor.RenderAsync("/Templates/Export/Packet.cshtml",
                new PacketView(user.DisplayName, helperName, questions, InlineCss.Load(ExportPipeline.AssetsDir, "packet.css")));
            await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), html, ct);
            var pdf = await gotenberg.HtmlToPdfAsync(dir, waitForPagedJs: false, outline: false, ct);
            audit.Add("packet.print", $"Printed a paper packet of {questions.Count} questions for {user.DisplayName}", "user", storytellerId);
            await db.SaveChangesAsync(ct);
            return (pdf, questions.Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
