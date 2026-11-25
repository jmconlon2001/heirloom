using FamilyMemories.Data;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class PromptSeedTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Seeds_52_prompts_in_12_chapters_with_26_core()
    {
        Assert.Equal(12, await app.WithDbAsync(db => db.Chapters.CountAsync()));
        Assert.Equal(52, await app.WithDbAsync(db => db.Prompts.CountAsync()));
        Assert.Equal(26, await app.WithDbAsync(db => db.Prompts.CountAsync(p => p.IsCore)));
        var hints = await app.WithDbAsync(db => db.Prompts.Select(p => p.Hints.Count).ToListAsync());
        Assert.All(hints, h => Assert.InRange(h, 2, 4));
        var sets = await app.WithDbAsync(db => db.PromptSets.Select(s => s.Kind).ToListAsync());
        Assert.Contains(PromptSetKind.Core, sets);
        Assert.Contains(PromptSetKind.Full, sets);
    }

    [Fact]
    public async Task Seeding_again_changes_nothing_and_keeps_admin_edits()
    {
        await app.WithDbAsync(async db =>
        {
            var p = await db.Prompts.SingleAsync(x => x.Key == "roots-born");
            p.Question = "Where were you born? (edited)";
            p.EditedByAdmin = true;
            await db.SaveChangesAsync();
        });

        await app.WithServiceAsync<PromptSeeder, bool>(async s => { await s.SeedAsync(); return true; });
        await app.WithServiceAsync<PromptSeeder, bool>(async s => { await s.SeedAsync(); return true; });

        Assert.Equal(52, await app.WithDbAsync(db => db.Prompts.CountAsync()));
        Assert.Equal(2, await app.WithDbAsync(db => db.PromptSets.CountAsync()));
        Assert.Equal("Where were you born? (edited)",
            await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == "roots-born").Select(p => p.Question).SingleAsync()));
    }
}

public sealed class StoryPlanTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private Task<StoryPlan> Plan(Guid userId) => app.WithServiceAsync<StoryPlanService, StoryPlan>(s => s.GetAsync(userId));

    private async Task SetStatusAsync(Guid userId, Guid promptId, MemoryStatus status)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var memories = scope.ServiceProvider.GetRequiredService<MemoryService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prompt = await db.Prompts.SingleAsync(p => p.Id == promptId);
        var memory = await memories.GetOrCreateForPromptAsync(userId, prompt, userId);
        await memories.SetStatusAsync(memory.Id, status, userId);
    }

    [Fact]
    public async Task Core_set_has_26_prompts_in_chapter_order()
    {
        var rose = await app.CreateUserAsync("Rose", "rose-core@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var plan = await Plan(rose.Id);
        Assert.Equal(26, plan.Slots.Count);
        Assert.Equal("Where and when were you born? Do you know how your name was chosen?", plan.Slots[0].Prompt.Question);
        Assert.Equal("Write a letter to future generations of our family.", plan.Slots[^1].Prompt.Question);
        Assert.Equal(12, plan.Chapters.Count);
    }

    [Fact]
    public async Task Weekly_pacing_unlocks_one_new_prompt_each_week()
    {
        var rose = await app.CreateUserAsync("Rose", "rose-weekly@example.test", [Roles.Storyteller], PromptSetKind.Full, Pacing.Weekly);

        var plan = await Plan(rose.Id);
        Assert.Equal(1, plan.Slots.Count(s => s.Unlocked));
        Assert.Equal(plan.Slots[0], plan.Next);
        Assert.Equal(rose.PacingStartedAt!.Value.AddDays(7), plan.Slots[1].UnlocksAt!.Value, TimeSpan.FromSeconds(1));

        app.Clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(2, (await Plan(rose.Id)).Slots.Count(s => s.Unlocked));
        app.Clock.Advance(TimeSpan.FromDays(14));
        Assert.Equal(4, (await Plan(rose.Id)).Slots.Count(s => s.Unlocked));
    }

    [Fact]
    public async Task All_at_once_opens_everything()
    {
        var rose = await app.CreateUserAsync("Rose", "rose-all@example.test", [Roles.Storyteller], PromptSetKind.Full, Pacing.AllAtOnce);
        Assert.All((await Plan(rose.Id)).Slots, s => Assert.True(s.Unlocked));
    }

    [Fact]
    public async Task Next_prompt_skips_finished_ones_and_brings_skipped_back_last()
    {
        var rose = await app.CreateUserAsync("Rose", "rose-next@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var plan = await Plan(rose.Id);
        var (first, second, third) = (plan.Slots[0].Prompt.Id, plan.Slots[1].Prompt.Id, plan.Slots[2].Prompt.Id);

        await SetStatusAsync(rose.Id, first, MemoryStatus.Skipped);
        await SetStatusAsync(rose.Id, second, MemoryStatus.Complete);
        await SetStatusAsync(rose.Id, third, MemoryStatus.Declined);
        plan = await Plan(rose.Id);
        Assert.Equal(plan.Slots[3].Prompt.Id, plan.Next!.Prompt.Id);
        Assert.Equal(1, plan.SharedCount);

        foreach (var slot in plan.Slots.Skip(3))
            await SetStatusAsync(rose.Id, slot.Prompt.Id, MemoryStatus.Complete);
        plan = await Plan(rose.Id);
        Assert.Equal(first, plan.Next!.Prompt.Id); // the skipped one comes back
        Assert.False(plan.IsComplete);

        await SetStatusAsync(rose.Id, first, MemoryStatus.Complete);
        plan = await Plan(rose.Id);
        Assert.Null(plan.Next);
        Assert.True(plan.IsComplete); // declined counts as done
    }

    [Fact]
    public async Task A_started_prompt_stays_open_under_weekly_pacing()
    {
        var rose = await app.CreateUserAsync("Rose", "rose-open@example.test", [Roles.Storyteller], PromptSetKind.Full, Pacing.Weekly);
        var later = (await Plan(rose.Id)).Slots[5].Prompt.Id;
        await SetStatusAsync(rose.Id, later, MemoryStatus.Draft);
        Assert.True((await Plan(rose.Id)).Slots[5].Unlocked);
    }

    [Fact]
    public async Task One_off_questions_come_first_with_who_asked()
    {
        var rose = await app.CreateUserAsync("Rose", "rose-oneoff@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.Weekly);
        var lily = await app.CreateUserAsync("Lily", "lily-oneoff@example.test", [Roles.Family]);
        await app.WithServiceAsync<FamilyMemories.Admin.UserAdminService, Prompt>(s =>
            s.AddOneOffPromptAsync(rose.Id, "What was the farm dog called?", null, lily.Id));

        var plan = await Plan(rose.Id);
        Assert.Equal(27, plan.Slots.Count);
        Assert.True(plan.Next!.IsOneOff);
        Assert.Equal("Lily", plan.Next.Prompt.AskedByUser!.DisplayName);
        Assert.Equal("Asked just for you", plan.Chapters[0].Title);
    }

    [Fact]
    public async Task Custom_sets_contain_exactly_the_chosen_prompts()
    {
        var ids = await app.WithDbAsync(db => db.Prompts.OrderBy(p => p.Key).Select(p => p.Id).Take(3).ToListAsync());
        var setId = Guid.NewGuid();
        await app.WithDbAsync(async db =>
        {
            db.PromptSets.Add(new PromptSet { Id = setId, Name = "Three", Kind = PromptSetKind.Custom, Items = [.. ids.Select(id => new PromptSetItem { PromptSetId = setId, PromptId = id })] });
            await db.SaveChangesAsync();
        });
        var rose = await app.CreateUserAsync("Rose", "rose-custom@example.test", [Roles.Storyteller]);
        await app.WithDbAsync(db => db.Users.Where(u => u.Id == rose.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PromptSetId, setId)));

        var plan = await Plan(rose.Id);
        Assert.Equal(ids.Order(), plan.Slots.Select(s => s.Prompt.Id).Order());
    }
}

public sealed class MemoryServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task<(AppUser Rose, Memory Memory)> StartAsync(string email)
    {
        var rose = await app.CreateUserAsync("Rose", email, [Roles.Storyteller], PromptSetKind.Core);
        var memory = await app.WithServiceAsync<MemoryService, Memory>(async s =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var prompt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Prompts.SingleAsync(p => p.Key == "roots-born");
            return await s.GetOrCreateForPromptAsync(rose.Id, prompt, rose.Id);
        });
        return (rose, memory);
    }

    private Task<SaveResult> Save(Guid memoryId, string body, Guid actor, VersionKind kind, int? revision = null) =>
        app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(memoryId, new MemoryEdit(null, body, default, null), actor, kind, revision));

    private Task<List<MemoryVersion>> Versions(Guid memoryId) =>
        app.WithDbAsync(db => db.MemoryVersions.Where(v => v.MemoryId == memoryId).OrderBy(v => v.Number).ToListAsync());

    [Fact]
    public async Task One_story_per_prompt_and_it_starts_with_the_prompt_title()
    {
        var (rose, memory) = await StartAsync("mem-one@example.test");
        var again = await StartAsync("mem-one-b@example.test");
        Assert.Equal("Where you were born", memory.Title);
        Assert.Equal(MemoryStatus.NotStarted, memory.Status);
        Assert.Equal(Visibility.AllFamily, memory.Visibility);
        Assert.NotEqual(memory.Id, again.Memory.Id);

        var same = await app.WithServiceAsync<MemoryService, Memory>(async s =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var prompt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Prompts.SingleAsync(p => p.Key == "roots-born");
            return await s.GetOrCreateForPromptAsync(rose.Id, prompt, rose.Id);
        });
        Assert.Equal(memory.Id, same.Id);
    }

    [Fact]
    public async Task Autosaves_are_coalesced_but_every_save_is_kept()
    {
        var (rose, memory) = await StartAsync("mem-versions@example.test");

        await Save(memory.Id, "I was born", rose.Id, VersionKind.Autosave);
        await Save(memory.Id, "I was born in Ohio", rose.Id, VersionKind.Autosave);
        await Save(memory.Id, "I was born in Ohio in 1940", rose.Id, VersionKind.Autosave);
        var versions = await Versions(memory.Id);
        Assert.Single(versions);
        Assert.Equal("I was born in Ohio in 1940", versions[0].Body);

        await Save(memory.Id, "I was born in Ohio in 1940.", rose.Id, VersionKind.Save);
        await Save(memory.Id, "I was born in Ohio in 1940. On a farm.", rose.Id, VersionKind.Save);
        Assert.Equal(3, (await Versions(memory.Id)).Count);

        // An autosave after the coalescing window starts a new version.
        app.Clock.Advance(MemoryService.AutosaveCoalesceWindow + TimeSpan.FromMinutes(1));
        await Save(memory.Id, "I was born in Ohio in 1940. On a farm. Near Akron.", rose.Id, VersionKind.Autosave);
        Assert.Equal(4, (await Versions(memory.Id)).Count);

        var saved = await app.WithDbAsync(db => db.Memories.SingleAsync(m => m.Id == memory.Id));
        Assert.Equal(MemoryStatus.Draft, saved.Status);
    }

    [Fact]
    public async Task Saving_unchanged_words_adds_no_version()
    {
        var (rose, memory) = await StartAsync("mem-unchanged@example.test");
        await Save(memory.Id, "Same words", rose.Id, VersionKind.Save);
        await Save(memory.Id, "Same words", rose.Id, VersionKind.Save);
        var result = await Save(memory.Id, "Same words", rose.Id, VersionKind.Autosave);
        Assert.Equal(SaveOutcome.Unchanged, result.Outcome);
        Assert.Single(await Versions(memory.Id));
    }

    [Fact]
    public async Task An_old_tab_cannot_overwrite_newer_words()
    {
        var (rose, memory) = await StartAsync("mem-conflict@example.test");
        var first = await Save(memory.Id, "Tab A", rose.Id, VersionKind.Autosave, memory.Revision);
        Assert.Equal(SaveOutcome.Saved, first.Outcome);

        var stale = await Save(memory.Id, "Tab B (old)", rose.Id, VersionKind.Autosave, memory.Revision);
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal("Tab A", await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memory.Id).Select(m => m.Body).SingleAsync()));
    }

    [Fact]
    public async Task Line_breaks_are_normalised_and_paragraphs_kept()
    {
        var (rose, memory) = await StartAsync("mem-lines@example.test");
        await Save(memory.Id, "First paragraph.\r\n\r\nSecond line one\r\nline two   \r\n", rose.Id, VersionKind.Save);
        Assert.Equal("First paragraph.\n\nSecond line one\nline two",
            await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memory.Id).Select(m => m.Body).SingleAsync()));
    }

    [Fact]
    public async Task Restoring_brings_back_old_words_and_keeps_the_current_ones()
    {
        var (rose, memory) = await StartAsync("mem-restore@example.test");
        var admin = await app.CreateUserAsync("Admin", "mem-restore-admin@example.test", [Roles.Admin]);
        await Save(memory.Id, "Original words", rose.Id, VersionKind.Save);
        await Save(memory.Id, "Oops, deleted everything", rose.Id, VersionKind.Save);

        Assert.True(await app.WithServiceAsync<MemoryService, bool>(s => s.RestoreVersionAsync(memory.Id, 1, admin.Id)));

        var versions = await Versions(memory.Id);
        Assert.Equal(["Original words", "Oops, deleted everything", "Original words"], versions.Select(v => v.Body));
        Assert.Equal(VersionKind.Restore, versions[^1].Kind);
        Assert.Equal("Original words", await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memory.Id).Select(m => m.Body).SingleAsync()));
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "memory.restore")));
    }

    [Fact]
    public async Task Finishing_marks_complete_and_keeps_a_version()
    {
        var (rose, memory) = await StartAsync("mem-finish@example.test");
        await Save(memory.Id, "Done!", rose.Id, VersionKind.Autosave);
        await app.WithServiceAsync<MemoryService, bool>(s => s.SetStatusAsync(memory.Id, MemoryStatus.Complete, rose.Id));

        var saved = await app.WithDbAsync(db => db.Memories.SingleAsync(m => m.Id == memory.Id));
        Assert.Equal(MemoryStatus.Complete, saved.Status);
        Assert.NotNull(saved.CompletedAt);
        Assert.Equal(VersionKind.Save, (await Versions(memory.Id)).Single().Kind); // the autosave was promoted
    }

    [Fact]
    public async Task Proxy_writing_is_attributed_and_audited()
    {
        var (rose, memory) = await StartAsync("mem-proxy@example.test");
        var jacob = await app.CreateUserAsync("Jacob", "mem-proxy-jacob@example.test", [Roles.Family]);
        await Save(memory.Id, "Rose told me this", jacob.Id, VersionKind.Save);

        var saved = await app.WithDbAsync(db => db.Memories.SingleAsync(m => m.Id == memory.Id));
        Assert.Equal(jacob.Id, saved.WrittenById);
        Assert.Equal(rose.Id, saved.StorytellerId);
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "memory.proxy-write" && a.TargetId == memory.Id.ToString())));
    }
}
