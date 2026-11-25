using System.Text.Json;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Stories;

/// <summary>
/// Loads seed/prompts.json at startup. Idempotent upsert by stable key: new chapters and prompts
/// are added, and seed text changes flow through, but never over a prompt an admin has edited.
/// Also makes sure the built-in "Core 26" and "Full 52" sets exist.
/// </summary>
public sealed class PromptSeeder(AppDbContext db, TimeProvider clock, ILogger<PromptSeeder> log)
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "seed", "prompts.json");

    private sealed record SeedFile(List<SeedChapter> Chapters);
    private sealed record SeedChapter(string Key, string Title, List<SeedPrompt> Prompts);
    private sealed record SeedPrompt(string Key, bool Core, string Title, string Question, List<string> Hints, string? SuggestedAttachments);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task SeedAsync(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            log.LogWarning("Prompt seed file {Path} not found; skipping prompt seeding", path);
            return;
        }

        await using var stream = File.OpenRead(path);
        var seed = await JsonSerializer.DeserializeAsync<SeedFile>(stream, Json)
            ?? throw new InvalidOperationException($"Could not read {path}");

        var now = clock.GetUtcNow().UtcDateTime;
        var chapters = await db.Chapters.Where(c => c.Key != null).ToDictionaryAsync(c => c.Key!);
        var prompts = await db.Prompts.Where(p => p.Key != null).ToDictionaryAsync(p => p.Key!);
        int added = 0, updated = 0;

        for (var ci = 0; ci < seed.Chapters.Count; ci++)
        {
            var sc = seed.Chapters[ci];
            if (!chapters.TryGetValue(sc.Key, out var chapter))
            {
                chapter = new Chapter { Id = Guid.CreateVersion7(), Key = sc.Key };
                db.Chapters.Add(chapter);
                chapters[sc.Key] = chapter;
            }
            chapter.Title = sc.Title;
            chapter.SortOrder = (ci + 1) * 10;

            for (var pi = 0; pi < sc.Prompts.Count; pi++)
            {
                var sp = sc.Prompts[pi];
                if (!prompts.TryGetValue(sp.Key, out var prompt))
                {
                    prompt = new Prompt { Id = Guid.CreateVersion7(), Key = sp.Key, CreatedAt = now, IsActive = true };
                    db.Prompts.Add(prompt);
                    prompts[sp.Key] = prompt;
                    added++;
                }
                else if (prompt.EditedByAdmin)
                {
                    continue;
                }
                else
                {
                    updated++;
                }

                prompt.ChapterId = chapter.Id;
                prompt.Title = sp.Title;
                prompt.Question = sp.Question;
                prompt.Hints = [.. sp.Hints];
                prompt.SuggestedAttachments = sp.SuggestedAttachments;
                prompt.IsCore = sp.Core;
                prompt.SortOrder = (pi + 1) * 10;
            }
        }

        foreach (var (kind, name) in new[] { (PromptSetKind.Core, "Core 26"), (PromptSetKind.Full, "Full 52") })
        {
            if (!await db.PromptSets.AnyAsync(s => s.Kind == kind))
                db.PromptSets.Add(new PromptSet { Id = Guid.CreateVersion7(), Kind = kind, Name = name, CreatedAt = now });
        }

        await db.SaveChangesAsync();
        if (added > 0) log.LogInformation("Seeded {Added} new prompts ({Updated} checked for updates)", added, updated);
    }
}
